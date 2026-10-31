# Architecture and training notes

## Components

- **Core (`net8.0`):** `NeuralNetwork` implements dense feed-forward layers, sigmoid activation, Xavier-uniform initial weights, per-sample stochastic gradient descent, and backpropagation. The core has no UI, ML, or NuGet package dependencies.
- **App (`net8.0-windows`):** the WPF window binds to `MainViewModel`. The view model handles the four-step guided XOR lesson, its baseline predictions, experiment settings, asynchronous training, cancellation, snapshots, metrics, and model persistence through `IModelFileService`.
- **Controls:** `NeuralNetworkView` draws connections from detached snapshots (cyan = positive, purple = negative); `LossChartView` plots mean squared error with a logarithmic y-axis.
- **Core SmokeTests (`net8.0`):** plain console assertions for core correctness.
- **App SmokeTests (`net8.0-windows`):** starts the real window on the Windows runner and tests bindings, the beginner guide, a short real training session, and reset behavior. No external test framework is required.

## Training

For a neuron in layer `l`:

```text
z[l] = W[l] * a[l-1] + b[l]
a[l] = sigmoid(z[l])
sigmoid(z) = 1 / (1 + exp(-z))
```

Training computes derivatives from the output backwards. The output delta for mean squared error is proportional to `(prediction - target) * sigmoid'(z)`; the factor of 2 is absorbed into the learning rate. Hidden deltas use the **old**, not yet updated, downstream weights. Only after all deltas have been calculated do we update weights and biases.

For each sample:

```text
W[l] -= learningRate * delta[l] * a[l-1]
b[l] -= learningRate * delta[l]
```

All current experiments use sigmoid activations, mean squared error, and stochastic gradient descent (not mini-batches or Adam). The default XOR network has topology `2 → 4 → 1` and trains on all four truth-table rows once per epoch.

## UI and threading

The model is trained on a worker thread. Periodic progress updates carry **deep-copied snapshots** onto the WPF dispatcher. The UI never directly reads parameters as they are being mutated by the training thread. `CancellationToken` is checked before each epoch. Training configuration and model file operations are disabled as commands while training runs, but text inputs can still be edited; their new values apply at the next run. The beginner-friendly **Watch training live** option paces progress reports on the worker thread so visualization updates are visible; it can be turned off for full-speed training.

Progress callbacks are explicitly dispatched to the captured WPF `Dispatcher`, even when the caller has no `SynchronizationContext` (such as the standalone STA smoke test). Final state is applied with `Dispatcher.InvokeAsync` and stale callbacks are rejected by run ID and training state. This prevents cross-thread CollectionView failures and an old update overwriting the final comparison after `Stop` or `Reset`.

## Guided lesson and baseline

- The guide has four stages: XOR truth table, starting guesses, training, and results. Users can move backwards at any time while idle. Step 4 is unavailable until at least one epoch has been trained.
- At construction, reset, new topology/seed, or model load, the ViewModel captures a detached **baseline** of four predictions plus their mean squared error. The UI computes accuracy and per-example changes relative to that immutable starting point; subsequent training does not change it.
- `Restart guide` only changes the instructional step. `Reset` discards learned weights and creates a fresh baseline; model loading creates a baseline at the loaded model's current weights.
- The `Before` and `Now` values, change in loss, and correctness metrics always come from real network snapshots. They are **training-set metrics** on just four examples, not a generalization score or likelihood of future performance.
- `ProgressBar.Value` bindings targeting read-only ViewModel/prediction properties explicitly use `Mode=OneWay`. An STA Windows UI smoke test verifies this to catch runtime binding failures that compilation alone misses.


## Model file

`ModelPersistence` serializes a versioned JSON envelope with `FormatVersion = 1` and `Network = { LayerSizes, Weights, Biases }`. Loading rejects malformed shapes, nonfinite parameters, and unsupported versions. The XOR UI additionally restricts loaded topologies to two inputs, 2–12 hidden neurons, and one output. Saving a model records its parameters, **not** a dataset, training history, or random seed.

## Constraints

- This is an educational learning engine, **not** a large language model or a general-purpose AI assistant.
- The XOR dataset is intentionally tiny; low error on it does not establish generalization.
- The loss chart uses logarithmic scaling; the points correspond to periodically reported epochs, not every epoch.
- Current implementation is CPU-only and intentionally avoids cloud services.

## Planned experiments

1. Custom datasets / train-validation split / experiment export.
2. Local vector embeddings for semantic document search.
3. Log and telemetry anomaly detection with baseline comparisons.

## Smart Search Lab (v0.2.0)

- **Search library (`net8.0`):** `LocalTextLoader` imports bounded text files in cancellable ~32K-character reads with byte-based import progress. Auto mode performs strict UTF-8 decoding with Unicode BOM detection; users can explicitly override decoding to Windows-1251, Windows-1252 or BOM-less UTF-16LE/BE, `TextChunker` splits documents into 1,800-character windows with 180-character overlap, `KeywordSearch` counts whole-word query-term occurrences with cancellation, suppresses frequent question filler words and prioritizes distinct matched terms, and `SemanticSearch` builds a normalized vector index and computes cosine similarity. The public `IEmbeddingProvider` interface keeps model inference interchangeable.
- **Local model adapter:** `OllamaEmbeddingProvider` sends only to local loopback HTTP (`127.0.0.1:11434/api/embed`) by default, using EmbeddingGemma. It posts arrays of inputs in batches of eight and validates counts, vector sizes, finite values, and nonzero norms. The UI exposes an explicit connection check and a user-copied model installation command. It never downloads or uploads files on its own.
- **Retrieval prompts:** for documents, `title: {document name} | text: {chunk}`; for queries, `task: search result | query: {query}`. The model supports multilingual text; cross-language quality depends on model/data and is not guaranteed.
- **UI:** `SmartSearchView` is a second navigable lab within `MainWindow`, with its own `SmartSearchViewModel`. It exposes sample text, file import, AI index creation with progress/cancellation, and side-by-side raw lexical versus semantic results. The lab works in a keyword-only mode if Ollama is unavailable, without presenting fabricated semantic results.
- **Index lifecycle:** queryable indexes are assembled in memory and become invalid when the document set changes. They carry the embedding model name so query embeddings cannot accidentally be compared to vectors from a different model. Document import and text splitting are cancellable and publish the new set atomically, preserving the previous set on failure. Parsed chunks are cached, avoiding repeated split work on the UI thread; keyword scanning runs on a worker. Indexing checks cancellation between embedding batches; a completed in-memory index is published atomically. Each completed batch of eight normalized float vectors is also checksummed and written via a uniquely named temporary file and atomic rename in `%LOCALAPPDATA%\BadWolfAIPlayground\SearchIndexes`. The key is SHA-256 over exact ordered chunks, source identity, the retrieval-format version and model fingerprint, including Ollama's **digest** from `/api/tags`. Cached batches are validated (structure, dimension, finite values, normalized magnitude, checksum) before reuse; corrupt batches are individually recomputed. Interrupted builds preserve completed batches; resumed builds reassemble the in-memory index without repeating successful Ollama embeddings. One index is bounded to 25,000 chunks (up to 16 MiB per input file, 32 MiB total, 24 million characters). The UI displays the approximate number of eight-chunk batches and per-batch progress. Building a large index can take minutes and requires free RAM for the document text, chunks, and vectors.
- **Scoring:** lexical results are ranked by term occurrences; semantic results use normalized dot product, equal to cosine similarity. A value like `0.71` is **not a 71% probability**. Review the retrieved source before trusting any result.
- **Security and limitations:** only user-selected local text files are read; text import enforces file count, size and decoded character limits, rejecting NULs and invalid sequences in the selected encoding. Legacy code pages are never guessed automatically, since incorrect decoding may return plausible but incorrect search hits. The import card displays failures inline, retains the true exception message and preserves the previous document set on failure or cancellation. Search does not execute file content or follow internet links. A model's local endpoint may write its own logs/cache as configured by Ollama. Document text itself is not written to the checkpoint cache, but the local binary embedding vectors may reveal information and are not encrypted. The most recent selected paths and encoding are stored locally in `last-search-files.json` and only read on user-initiated **Restore previous files**. Neither source text nor files are loaded automatically on startup. Old caches are not automatically deleted; remove the local files from Explorer if needed. OCR, PDF import, and background file watching are not included.
- **Verification:** package-free search smoke tests use a deterministic mock embedding provider, an HTTP handler stub for Ollama, chunk boundaries, multi-megabyte UTF-8 import, byte-based progress, legacy Windows encodings, explicit BOM-less UTF-16, actionable failures, safety limits, batch indexing, cancellation, cross-language ranking behavior, and validation. The Windows WPF smoke test navigates between labs, verifies keyword search with Ollama absent, and imports a synthetic 3.5 MiB file, checks actual error display and retries a Windows-1251 file to catch regressions in UI responsiveness, threading, encoding and file limits. Search smoke tests also simulate an interrupted build, saved-batch resumption, a fully cached index, changed text/model digests, corrupt checkpoint recovery and Ollama fingerprint validation. The WPF smoke test checks ComboBox dark-theme contrast and restoring the previous selected files/encoding after a new ViewModel is created. Actual embedding model performance still requires a manual local test with EmbeddingGemma installed.

## Grounded document answers (optional Ollama model)

The **Compare searches** mode intentionally shows only keyword matches and
cosine-similarity rankings. Those scores are not correctness probabilities,
and the top-ranked text may have no answer to a natural-language question.

**Ask document** uses an additional, user-installed local generator
(`qwen3:4b-instruct`, via Ollama `/api/chat`). EmbeddingGemma remains the
separate vector-retrieval model. The answer pipeline:

1. Propose at most two alternative retrieval phrases with a local chat model.
   These phrases are search hints, **not verified facts**.
2. Search both the original and rewritten phrases using dense embeddings
   and lexical matching. Merge candidates by reciprocal-rank fusion.
3. Send the seven best snippets (not the whole file) to the local chat model
   with source IDs and an instruction to use only those snippets.
4. Parse structured JSON with `supported`, `answer`, and verbatim
   `{id, quote}` evidence. Verify that every cited substring exists in
   the referenced original chunk, allowing only whitespace normalization.
5. Show cited answers with source filenames/chunk IDs; otherwise show an
   explicit "not found in selected text" message. Never return an
   unsupported answer as if it were verified.

The source documents are untrusted input; instructions embedded in them
must be treated as data, and no tool or filesystem action is driven by
model-generated content. Quote matching is only an authenticity check:
it cannot guarantee a correct inference or a relevant answer. Source
omissions, translation differences, incomplete passages, contextual
ambiguities and model quality still matter. The WPF UI labels the
response as AI-generated and asks users to read the quotes. A missing
answer model generates an actionable Ollama installation error; no model
download or cloud call is automatic.

Search-library smoke tests use fake answer/embedding models and mocked
Ollama `/api/chat`, verify query expansion, source/citation validation,
unsupported-answer handling, and local JSON response parsing. They do
not measure the quality of the real Qwen3 model.


### Bounded local answer context

Ollama's local default may be 4,096 tokens depending on available VRAM.
The optional Ollama answer provider explicitly requests `num_ctx=8192`
through `/api/chat`, while bounding each retrieved source excerpt to
900 original characters and preserving up to seven labeled evidence IDs.
It favors a window around significant exact query terms when possible.
The source quotation check still uses the complete underlying chunk.
The semantic index and on-disk embedding checkpoints are unaffected by
this answer-only change. Invalid context size responses are explained
as a context setting issue rather than misleading the user into
reinstalling an already working model. Ask-document failures are shown
only in the answer panel instead of overwriting Compare-searches status.
The HTTP smoke test checks explicit context length, bounded input
and friendly recovery from an Ollama 400 context-window error.


### Inspecting retrieval and failure reasons

The grounded Q&A pipeline surfaces `AnswerOutcome`: `Verified`,
`NoCandidates`, `ModelDeclined`, or `UnverifiableCitation`.
Even unsuccessful model answers return the original search queries and all
candidate passages so the WPF evidence expander can display exactly
what was sent to the local model. This distinguishes failed retrieval
from a model refusal and from invalid generated quotations, without
pretending that the original source lacks the answer.

Diverse selection reserves up to two keyword candidates and two
embedding candidates from the **original** question before adding
remaining candidates by reciprocal-rank fusion across rewritten
queries. This protects rare exact query anchors when semantic ranking
is noisy. All shown excerpts come directly from the indexed document;
the quotation verifier checks against that same clipped excerpt,
not hidden content elsewhere in the parent chunk. UI and engine
smoke tests verify result statuses, evidence provenance, candidate
limits, and exact-anchor retention with intentionally noisy
semantic vectors. Real model relevance remains unverified without
an end-to-end test against a supplied source corpus.


### File-decoding corruption guard and Q&A latency

StreamReader's single-byte codepage decoders may successfully decode UTF-8
as Windows-1251 while producing mojibake. A sampled strict UTF-8 validation
now rejects legacy decoding if the file is convincingly UTF-8, and a
post-decode high-frequency marker heuristic rejects texts that were
already incorrectly decoded/saved. Imports remain atomic: a rejected
file does not replace the last valid document set. WPF displays a
260-character original-text preview before the user builds an embedding
index, helping to spot errors without running either model.

Default local Q&A skips the initial, sequential Qwen query rewriting call.
The original question is ranked with keyword + embeddings and sent to
a single local Qwen answer request. An opt-in WPF checkbox re-enables
the expensive query expansion for deeper searches when needed. This
lowers expected latency but real GPU/offloading performance and relevance
need user-side validation on actual source text.


### Runtime model selection and measured performance

`OllamaRuntimeClient` reads Ollama's loopback `GET /api/tags` for
local model names/sizes, and `GET /api/ps` for currently loaded model
memory placement (`size_vram`, `size`, `context_length`).
`SmartSearchViewModel.SelectedAnswerModel` changes only the optional
`OllamaAnswerProvider` generation model; it never changes the
EmbeddingGemma index. The UI disables changes while a request is
running to avoid mixing results between different generation models.
The default remains `qwen3:4b-instruct`, with small CPU-oriented
alternatives shown as copyable `ollama pull` commands (no download
performed by the app).

`GroundedQuestionAnswering` separately measures retrieval time
(including optional query rewriting) and generation wall time.
`OllamaChatMetrics` parses Ollama's non-streamed response
`prompt_eval_count`, `eval_count`, `eval_duration`,
`load_duration` and `total_duration` (nanoseconds), with
`eval_count * 1e9 / eval_duration` as output tokens/second.
The WPF panel also reports total elapsed time. Processor placement
is from Ollama's loaded model API, not inferred from the computer's
graphics card or assumed from model parameter count. Unloaded models
and absent metrics remain *unknown*.

Search-library smoke tests use stub local HTTP responses to check
installed model discovery, processor reporting (CPU/GPU/unloaded),
model selection forwarded into `/api/chat`, and output token math.
The Windows WPF smoke test checks the picker and performance panel.
Real CPU throughput and answer quality remain manual user-side checks.


### Two-stage adaptive retrieval and accounting

`GroundedQuestionAnswering.AskAsync` retrieves and ranks up to seven
source candidates **once**. The ordered candidate set reserves slots for
the best original-question whole-word and dense matches before filling
from the fused ranking. It sends the first three candidates with a
480-character budget each to the local generator; only if this result
is absent or has no verbatim citation does it try the complete
candidate set once, bounded to 700 characters each. The same stable
source IDs and source-document identity are maintained between
attempts. A retry is skipped if there are no new passages **and**
the existing passages cannot provide additional source characters;
a single long passage can still be expanded on the second attempt.
Both the generator and the verifier use each
`EvidencePassage.ExcerptCharacters` value, preventing citation checks
against text not actually submitted to the model. The evidence
inspector displays only the final submitted set. No third attempt is
permitted. Cancellation is checked before every model call.

`GroundedAnswer` carries answer-attempt count, candidate count,
cumulative submitted passage count, cumulative excerpt characters
and the full-context character baseline. These are deterministic
*character* measurements, not estimated token or speed savings.
`OllamaAnswerProvider.BeginQuestion` clears the metric series and
records all subsequent successful `/api/chat` replies; Qwen query
rewrites and fallback generations are accumulated by
`OllamaChatMetrics.Sum`. If any measurement field is missing in one
reply, its aggregate remains unknown rather than being set to zero.
The WPF metrics panel shows the actual prompt/output tokens,
generation wall time, model load and rate across replies. First-pass
success avoids a second text-generation request; failure may cost
two requests and should be interpreted as a speed/recall tradeoff,
not a guaranteed optimization.

Deterministic tests cover initial success, a declined answer retried
with wider evidence, rejection of fabricated quotations after retry,
cancellation between attempts, accurate character counts and real
multi-response metric aggregation. Live CPU timings and correctness
on source books are manual acceptance tests.


### Answer-bearing citation validation

The original `GroundedQuestionAnswering.ValidateEvidence` confirmed that
a quote was copied from the candidate excerpt but never checked whether
that quote supported the answer. This led to a real-world bug: Qwen
answered `Лиса Микита` yet quoted a line about the fox that did not
mention the name.

The new `AnswerEvidenceValidator` operates *after* the exact-substring
provenance check, never instead of it. It identifies distinctive
non-stopword answer terms not already contained in the question,
and conservatively checks their occurrence in the quoted text. All
new names and numeric facts must be present, allowing narrow nominal
suffix variation for inflected forms. At least half of distinctive answer
terms must match the cited source text. Quotes that match no distinctive
answer term are filtered out rather than shown as supporting citations.

An unrelated verbatim citation triggers the existing single bounded
retry, not an additional independent LLM verification call. Failure
after the retry is reported as `UnsubstantiatedAnswer`, distinct from
`UnverifiableCitation` (no exact quote) and `ModelDeclined`.
Only quotes passing both provenance and lexical corroboration can be
displayed in the supported-answer area. The UI explains that matching
words/names is NOT semantic proof or an entailment guarantee; correct
paraphrases may need better textual evidence, while some semantically
misleading matches remain possible.

Search tests reproduce the user's *Farbovanyi Lys* mismatch with an
exact but irrelevant quote, verify positive and negative citations,
Ukrainian name inflections, missing years, one adaptive fallback
after weak evidence, and an unsupported outcome if retries remain weak.


### Avoiding slow repeated generation for irrelevant citations

A real Qwen2.5 1.5B CPU test regressed from a prior 13.77-second
successful answer to 47.05 seconds and no answer (two LLM calls).
The model's exact quote was unrelated to the generated proper name;
the previous validator always triggered another generation.

`AnswerEvidenceValidator.LocateSupportingQuote` now attempts bounded,
deterministic sentence extraction from the user's imported TXT after
at least one genuine model quote has passed exact-source validation.
It searches the submitted original chunks and up to 24 keyword
matches for the answer-specific words, selecting a quote that both
passes lexical fact checks and shares a topic word with the question.
No additional Ollama generation or embedding request is made. A
locally extracted quote is marked `VerifiedCitation.LocatedInSource`
and labeled distinctly in WPF, since Qwen did not cite it. Such a
quote may lie beyond the snippets actually sent to Qwen but must
come from an indexed user-selected source document.

An irrelevant but genuine model quote no longer triggers automatic
retry, even if local evidence is missing; the answer is marked
`UnsubstantiatedAnswer`. Refused answers and wholly invalid source
quotations still use the bounded second attempt when appropriate.
This prevents obvious wasted CPU work without weakening fabricated
citation rejection. It cannot fully prove semantic entailment.

Regression tests reproduce the user's false negative, local
one-call recovery, an answer absent from the book, an unrelated
same-name match, and a fact located after the clipped prompt excerpt.


### Deterministic source order as citation ranking tiebreaker

`AnswerEvidenceValidator.LocateSupportingQuote` previously used
`questionOverlap*10 + answerOverlap*5 + submittedPriority`.
The `submittedPriority` bonus could favor a later passage sent to
the local language model over the introductory sentence of the same
original book, despite identical evidence coverage.

The scorer now uses textual overlap alone for relevance, and resolves
equal-score citations by their index in `SemanticIndex.Chunks` and
then by the sentence's offset within that chunk. Thus the earliest
sufficiently supporting source sentence is preferred on a tie.
A bounded local scan also retains the first chunk mentioning the
proposed distinctive answer terms, preventing top-24 frequency-based
retrieval from hiding an introductory mention. This happens only
after the ordinary answer model has produced a genuine but
irrelevant quote; it adds no additional Qwen or embedding call.
More relevant later evidence remains preferred to weaker early
mentions. Test coverage includes the user's exact fox-name
introductory sentence and the later chunk-2 citation.


## Anomaly Detection Lab (v0.3.0)

### Opt-in local Ollama explanation of source-aware evidence (v0.4.0)

InvestigationPromptBuilder.Build uses the already selected
SourceInvestigationCase to create an in-memory evidence packet:
primary LOG line L1, at most six related LOG entries L2-L7 and
three indexed C# candidates C1-C3. All preserve original
file/physical-line references and bounded excerpts, with a
7,200-character maximum user message. It never loads entire
files or includes disabled/unimported sources. Untrusted log
strings and C# comments are explicitly identified as data,
not instructions. The exact system and user message text is
rendered in a read-only WPF preview before the request.

LocalOllamaInvestigationClient uses the same loopback Ollama
HTTP /api/chat protocol as Smart Search Lab, with non-streamed
JSON responses, num_ctx=8192 and a limited output budget.
The default HttpClient uses only 127.0.0.1:11434, with proxies
and redirects disabled; non-loopback injected clients are
rejected. Responses exceeding 64 KiB, malformed JSON and
unknown or absent evidence citations are refused. Every
hypothesis must cite a genuine packet ID, but even a valid
ID cannot establish the truth of a suggested root cause.
A no-hypothesis reply can legitimately explain missing evidence.

The new LocalInvestigationViewModel partial class adds an
explicit per-case Prepare button, installed model selection
via the existing OllamaRuntimeClient, question editing,
prompt preview, Explain button, and separate model
hypotheses, next checks and unknowns. Importing logs,
selecting source, preparing evidence or browsing models
**never** runs a chat inference call. Changing evidence
(Include/Exclude, import, source context, reanalysis)
invalidates the previous packet and model output.
Question editing also invalidates the preview so the
transmitted text always matches what the user reviewed.
The existing Cancel command interrupts the local request.

Synthetic engine smoke tests use a fake loopback HTTP handler,
check exact outgoing JSON, prompt limits, real and invented
citations, honest uncertainty, unsafe endpoints and
cancellation. Windows WPF smoke tests verify actual UI
controls and that preview/prepare remain local without
sending a request. Real-model behavior still requires
manual verification on the user's own machine.


### Bounded local context for ID-less log investigations (v0.5.0)

`SourceInvestigationAnalyzer.Search` retains its strict operation_id-only
**cross-component correlation**. For a selected record with no operation_id,
a separate same-file context pass indexes only the already enabled/retained
`LogParseResult.Events` for the chosen original source files. It gathers at
most three prior and three subsequent parsed entries within 60 physical
lines and +/-120 seconds. Entries with an unrelated explicit operation_id
are excluded, and no same-timestamp entries from a different file are
joined. It does not open any new file or inspect unimported records.
The per-file lookup is built once per bounded search rather than rescanned
separately for every selected case.

These entries are stored on `SourceInvestigationCase.NearbyEvents`, not in
`RelatedEvents`. `InvestigationPromptBuilder` admits up to six additional
LOG entries shared between true operation-correlated events and nearby
context, deduplicating timestamp+message mirrored observations. The
read-only audit and exact Ollama prompt label each nearby record explicitly
as **NOT operation-correlated**. The model's system instruction forbids
treating physical/time proximity as a runtime call, shared transaction or
cause, and forbids claiming missing logs do not exist.

`LocalOllamaInvestigationClient` additionally restricts L2+ citations
eligible to support a proposed *causal* hypothesis to actual
`Related LOG` evidence. Same-file context entries are not qualifying
corroboration merely by being adjacent. They can, however, inform the
model's suggested next diagnostic checks. A deterministic unknown notes
the limitation. Every hypothesis continues to require an actual allowed
citation, and no semantic root cause is proven by this policy.

A synthetic regression case tests same-file previous/next inclusion,
window bounds, explicit foreign operation exclusion, different-file
isolation, correct audit labels, non-causal citation enforcement, and
refreshing the prompt from a reduced included-only event set. CI does
not exercise proprietary production logs.

### Local Ollama structured-response interoperability (v0.5.0 follow-up)

`LocalOllamaInvestigationClient.ExplainAsync` now sends a JSON Schema
as `format` on the first `/api/chat` request. The schema requires
`summary`, `hypotheses`, `next_checks` and `unknowns`, constrains
array sizes, and enumerates the *actual selected packet IDs* as the only
valid evidence reference values. The request sets `think=false`
(avoiding Qwen3 reasoning-token starvation), `temperature=0`,
`num_predict=1400` and `num_ctx=8192`. A second local attempt
uses the exact same previewed system/user message bodies and raises
`num_predict` to 2200. An HTTP 400 on the schema request can
trigger a single compatibility retry with `format="json"`.

The parser accepts an otherwise valid JSON answer wrapped in a single
Markdown code fence, and bounds overlong model arrays to three visible
hypotheses, five checks and four unknowns. It validates the citations
of **every published hypothesis**, including the existing L2/C# guard
support requirement. Surplus unshown model entries never become claims.
Raw responses remain capped at 64 KiB and neither an invalid citation
nor a malformed/partial JSON message can be published as an AI finding.

One user-initiated click can therefore send **at most two** local
loopback requests containing precisely the already previewed evidence.
If both attempts fail to produce a valid structured answer, the
client supplies a separately labelled **deterministic evidence-only
fallback**, with no AI-generated hypotheses and no false diagnosis.
The WPF status lists the request count and makes this distinction
visible. All retries support cancellation. CI covers mock Qwen3
truncation and fenced JSON, excessive arrays from small models,
HTTP 400 compatibility, and persistent malformed output; **actual
Ollama model quality requires manual Windows validation**.

### Code-aware local investigation, evidence audit and conservative claims (v0.5.0)

The selected `SourceInvestigationCase` remains the **only source** of model
context: no additional disk, private source, excluded LOG or cloud access is
introduced. `InvestigationPromptBuilder` selects one original LOG record,
at most six additional LOG entries (operation-correlated or nearby same-file
context), and at most three C# candidates.
Candidate selection first diversifies across source evidence kinds (logger
location, exact/formatted logging template, stack frame and method-name hint)
so repetitive caller locations cannot evict all logging-template evidence.
Only already indexed C# snippets are included, and the 7,200-character
user-message limit is preserved.

Each selected C# candidate carries its static method/symbol, a short site
excerpt, already indexed enclosing `if`/`catch` conditions and possible
direct invocation names. Conditions seen anywhere in a method-only match are
explicitly labelled as **not proven reached**. Caller/stack locations are
identified as location hints, not as the origin of an error. Only a few
additional nearby indexed physical source lines are used; the prompt never
loads a full method body or executes a C# call graph.

The `InvestigationPrompt.Audit` string reports original/related LOG counts,
included/indexed C# candidates, truncated evidence sets, absence of indexed
source and ambiguities. WPF binds this audit separately from the exact
read-only prompt preview. Editing the question or changing imported/source
data invalidates the audit along with the preview; no automatic Ollama
requests are made.

The system instruction now distinguishes LOG observations from static
source facts and unverified hypotheses, asks for discriminating checks on
specific branches and variables, and asks for no hypothesis on weak evidence.
This is complemented by a **structural parser gate** in
`LocalOllamaInvestigationClient.ParseExplanation`: when the packet lacks
both (a) a distinct related LOG observation and (b) an enclosing guard
at an exact/formatted logging source site, model hypotheses are discarded.
Its free-form summary is replaced with a deterministic location-only
statement; limitations identify the missing corroboration. If no C#
candidate was provided, an explicit missing-code note is always added.
All cited IDs must still be valid, even if the final hypothesis is withheld.

A second validation layer removes individual hypotheses that cite only
`L1` or non-guard logger/stack hints despite other supporting evidence
being available. It requires a direct citation to a distinct related LOG
entry (`L2` etc.) or to a logging-template C# candidate containing an
indexed enclosing condition. Source correlation additionally deduplicates
equal timestamp+message observations across log targets to avoid counting
NLog mirrors as independent evidence. These are conservative structural
safeguards, not general semantic entailment checks.

This gate cannot validate whether richer observations semantically prove a
root cause. For a packet with multiple log observations or a code guard, the
model's narrative remains unverified. No "printer disconnected" or similar
explanation should ever be reported as an observed fact solely from an
unqualified `Failed printing recharge card receipt: 0/2` entry.

Regression coverage includes a synthetic Roslyn-indexed count guard and
related invocation, two ambiguous C# template sites, redundant caller hints,
a single-record receipt failure and adversarial JSON with invented printer
causes. Windows WPF smoke asserts that the on-screen evidence audit is
bound to the prepared packet and invalidates with question changes.
CI continues to exercise stub local HTTP; **quality assessment against real
installed Ollama models must be completed on the user's Windows machine**.





`BadWolfAIPlayground.Anomaly` is a dependency-free .NET 8 module with
records, a conservative timestamp/severity log parser, a robust
median/MAD time-bucket outlier detector and a rare error-template
heuristic. `AnomalyViewModel` invokes parsing and detection on worker
threads, applies results through the captured WPF dispatcher, supports
cancellation, and retains the last valid result after a failed import.

The demo is 100% synthetic and deterministic. Production logs are
accepted only through explicit local selection and the existing
size-bounded file importer, and remain in memory. Nothing is
uploaded, stored in embedding caches, or sent to Ollama. The UI
exposes both timestamped results and counts of unrecognized lines.
Eight or more buckets are required before producing burst alerts;
the outlier threshold is median + sensitivity times a robust spread,
with a minimum additional count of 3 and a minimum total burst of 5.
Rarer error templates are labeled as investigation leads, not proven
incidents, and only evaluated with at least 25 errors.

Testing: a dedicated non-WPF anomaly smoke runner covers parser
accuracy, synthetic error spikes, normal baseline, rare templates,
sensitivity, insufficient history and cancellation. The Windows WPF
smoke test additionally opens the third experiment, executes the
demo, verifies actual finding cards and reanalyzes the same sample
with a changed sensitivity. Without actual work log samples, the
supported format and any root-cause interpretation remain provisional.


### Optional source-aware log triage

`LogSourceContextIndex` scans a locally chosen solution directory
for elementary C# logging statements, supports simple interpolated
templates with bounded variable spans, and records relative source
file paths and line numbers. That legacy index is still a simple
regex matcher; the new independent `SourceInvestigationIndex` adds
Roslyn syntax parsing. Neither index requires cloud access, cloning
or persisted embeddings.
A matched source location is labeled as a *possible* source, not
proof of execution; no source code or matched message is transferred
to the public repository. File size, file count, total bytes,
template count and regex timeouts are bounded; generated build
folders are excluded.

The optional WPF **Select local C# solution** action builds this
in-memory index on a worker thread and annotates evidence cards
with possible source locations when the logged message matches.
A nearby `C:\...\File.cs: line 123` DEBUG line can also supply
caller evidence if it was emitted within three physical lines and
three seconds of the finding. Neither matching scheme establishes
causation. Deterministic tests use only synthetic C# statements
and exercise exact, interpolated and irrelevant messages as well as
the WPF source-selection flow. The ability to understand business
rules still requires human-authored documentation or explicit
source-level investigation, not just a message-template match.



### Roslyn-powered source investigation (offline)

The optional **Source-aware log investigation** panel uses
`SourceInvestigationIndex` in addition to the existing lightweight
`LogSourceContextIndex`. The index scans only an explicitly selected
local C# solution directory, ignores common generated/build folders
and directory reparse points, and parses syntax without compiling,
loading assemblies or executing any source code. The independent
.NET 8 Anomaly library has a `Microsoft.CodeAnalysis.CSharp` 4.14.0
dependency for syntax extraction. Indexing has bounded scanned
files/bytes, logging-site and method counts and cancellation support.

Logging invocations are parsed as C# syntax, rather than inferred
by matching arbitrary source text. Recognized direct logger and
logging-helper methods with literal/interpolated/structured format
strings become anchored message patterns. An index entry records
the specific source line, enclosing method and type, a short snippet,
enclosing `if`/`catch` expressions and directly named invocations
within the containing method. **Calls and conditions are static
observations only**, not an evaluated runtime path.

`SourceInvestigationAnalyzer` builds bounded investigation cases
from the selected, already-deduplicated log report: top recurring
error patterns, explicitly named TOM_OP operations and exception
stack traces. Candidates are ranked by evidence category: an exact
logging literal, a format/interpolation match, a declared operation
method name, or an explicit stack-frame line that exists in the
selected checkout. A name match is never promoted into a proven
runtime invocation. Multiple matching sources remain visible
and **ambiguous**; no match remains **unmatched**. The scanner never
correlates events merely by time proximity or guesses business rules.

`AnomalyViewModel` stores the index in memory only and rebuilds
source cards on regular reanalysis, Include/Exclude filtering and
source selection. Source-opening commands call
`SourceInvestigationIndex.ResolveSourceLocation`, which requires
the requested file/line to be in the indexed file whitelist, checks
the canonical full path is beneath the chosen checkout, rejects
reparse-point files and confirms the file still exists. The original
log location uses the independent Logs-folder allowlist. Both are
passed as structured Notepad++ arguments, never concatenated into
shell commands.

This deterministic index feeds the **optional user-triggered**
local Ollama explanation (above); logs and C# are never published,
committed, or sent automatically.
Synthetic engine and real Windows WPF smoke tests cover exact
and formatted logging sites, method declarations, ambiguous
literals, unmatched evidence, stack references, source navigation
and the live investigate-on-selection UI lifecycle.



### Discoverable collapsed evidence and full-log investigation search

The default Source-aware section is intentionally a **bounded
statistical summary**, not a full-text search index. The ViewModel
now formats each collapsed `AnomalyInvestigationRow` with a short
message excerpt (without trailing operation/exception metadata)
and separate time, category, original filename:line, explicit
operation ID, and number of C# candidates. It does not disguise
`0 candidate(s)` as a failed operation.

`SourceInvestigationAnalyzer.Search` performs an on-demand,
case-insensitive scan over **every retained event** in the current
`LogParseResult` (including DEBUG and INFO). It matches message
text, captured continuation, source filename and mirror-target
aliases. This is separate from `AnomalyDetector`'s bounded
pattern/finding selection, so an older uncommon IOException
can be found even when it is absent from the top patterns
and four displayed exception cases. A query is 3–256 characters.
The scan counts all matching events but retains at most **40
recent operation/file-line groups**, preferring error messages
as their representative when several levels share an exact ID.
A bounded second pass gathers linked records only for those
visible exact `operation_id` values. File line numbers are
never inferred from textual proximity.

`AnomalyViewModel.SearchInvestigationsAsync` runs the scan on
a background task with cancellation and safely replaces the
visible investigation cards. The source context is optional;
search-only cases still display original log evidence and
Notepad++ navigation. `ClearInvestigationSearch` restores the
cached ordinary investigation summary without reloading logs.
The current query is recomputed from the selected report when
Include/Exclude, file import, source loading or sensitivity
reanalysis changes the evidence, preventing stale cross-file
links. A skipped/partially analyzed file cannot be searched
until it has actually been imported; search counts do not
pretend to cover missing bytes/lines. No network or LLM call
is used.

Synthetic engine and Windows WPF tests validate exact
operation_id queries, free-text IOException and component
filename search, App/WebClient mirror aliases, unrelated
same-timestamp IDs, no-code operation, input length and
cancellation, informative collapsed headers, live search
binding and clearing, and Include/Exclude refresh.

### Cross-component exception correlation (explicit operation_id only)

`SourceInvestigationAnalyzer` gathers explicit `operation_id` values
from the bounded, currently visible error pattern, stack/finding
and TOM_OP evidence. It scans the **selected, deduplicated**
`AnomalyReport.Source.Events` once for exact matching IDs, recording
the total matching event count and bounded per-ID samples and caller
DEBUG messages. Correlation does not use timestamps, message
similarity, session guessing or files omitted by import/Include/Exclude.
An App/WebClient mirrored NLog error is still counted once according
to existing mirror-deduplication rules. The WPF section lists
related log file/line references and opens them only through
`AnomalyViewModel.ResolveLogLocation`.

`SourceInvestigationIndex.FindLoggedCallerLocations` parses
logger-emitted Windows/Unix `<absolute .cs path>: line <n>` hints,
matching **only full relative source-path suffixes** from files
explicitly indexed in the selected C# tree; the given physical line
must exist. A split `.xaml.cs` path fragment is repaired only when
the resulting normalized complete suffix independently matches an
indexed file. Method names can be shown only if a Roslyn method
declaration **contains** that line. The original logged absolute
path is never accepted as a file-opening target. The candidate is
clearly labelled a verified **logger-reported location**, distinct
from a stack-frame location or a mere static logging template.

A shared ID is evidence of **correlation**, not proof of causation;
a C# logger/catch location is not necessarily where a socket,
HTTP, SSL or transport error originated. The UI keeps this warning
and the original source references visible. Regression tests use
synthetic App/WebClient messages with the same operation ID,
an unrelated same-timestamp event with a different ID, an archived
checkout path and a split XAML filename. They verify exact C# line
resolution, bounded evidence, mirror deduplication, and reanalysis
after disabling/re-enabling WebClient. No customer source or logs are
checked in.

### Importing a whole multi-component Logs directory

`LogFolderImporter.Import` discovers supported `.log` and `.txt` files
recursively, including `old/`. The file content is read incrementally
through a strict UTF-8/BOM-marked UTF-16 `StreamReader` into
`LogParser.Parse(TextReader)`, with FileShare.ReadWrite|Delete for active
logs. The source text is never collected into a giant string.
The old 16 MiB per-file, 256 MiB combined, and 512 analyzed-file
limits no longer apply. The parser preserves strict encoding, binary
checks, bounded individual lines/context and physical line provenance.

Source discovery remains bounded at 50,000 metadata entries, and
up to 1,000,000 dated events can be retained in a single report.
Candidates are analyzed in priority order (Operations first, then
current/root files, non-archive and archived files). Files that
cannot fit wholly in the remaining event budget are excluded with
a visible reason and original relative path; no partial transaction
is represented as a complete file. The skipped-file count is based
on actual excluded sources. WPF virtualizes imported and skipped
file lists and reports PARTIAL IMPORT only when data was excluded.

Every `LogEvent` now stores `SourceFile` (relative to the
chosen folder) and its physical `LineNumber`. Detection
findings preserve this provenance and the optional list of
`AlsoInFiles` aliases. Exact cross-file duplicate copies
(timestamp, explicit severity, message) are deduplicated;
repeated same-file events remain distinct. The component
is inferred conservatively from the NLog filename, not
from undocumented application semantics. This avoids
false spikes from mirrored general App and component targets.

The WPF view offers **Select Logs folder** alongside single-file
selection and the demo. A collapsible per-file/component
breakdown explains the difference between raw source-file
counts and deduplicated aggregate totals. Findings display
`relativePath:line`; code-caller DEBUG hints are restricted
to **the same source file** to avoid accidentally correlating
unrelated entries with matching physical line numbers.

The import is transactional at the view-model boundary: a
failed or canceled selection does not replace the previous
successful report. No absolute paths, file bodies, or
business-specific log templates are persisted, uploaded,
or added to this public repository. Tests build an entirely
synthetic multi-target Logs folder with nested archives,
identical mirrored NLog entries, provenance and failure cases.


**Conservative event identity:** exact equality alone is not enough
to merge two independent components. Cross-file deduplication is
restricted to a general `App` log paired with a dedicated
component target, or duplicate copies of the *same filename*
in different folders. Other components retain independent
events even at identical timestamps. A regression test guards
against accidental undercounting of simultaneous device events.


### Per-file filtering and precise local log navigation

A `LogFolderImportResult` retains bounded `RawEvents` in
memory and the initial deduplicated `Combined` report.
`LogFolderImporter.SelectFiles` validates selected paths
against the original imported file list and rebuilds
cross-target deduplication from **only the enabled raw
events**, recomputing per-file totals and duplicate counts.
This avoids incorrect counts and lost evidence when the
primary component target of a previously deduplicated
App/component pair is disabled. The import has a maximum
of 1,000,000 retained dated events, both raw and distinct. Selection never
causes a second disk read, copy of log text to a server,
embedding operation, or Ollama request.

`AnomalySourceFileRow.Included` is an observable state,
bound directly to per-log WPF checkboxes. Changing a file
triggers asynchronous, cancellable local recomputation of
the timeline and detection findings. Include all / Exclude
all allow bulk selection with one recomputation. While busy,
file controls are disabled to avoid contradictory results.
An empty selection explicitly shows no events. The previous
valid data remains available if a reanalysis fails.

`AnomalyFindingRow.SourceFile` and `SourceLine` preserve
original relative filename and physical line without relying
on display parsing. WPF finding actions pass the typed row
as a command parameter. `ResolveLogLocation` requires the
file to be part of the imported file list and verifies that
its normalized absolute path is below the selected root;
single-file mode has its own authorized path. The path
remains only in volatile ViewModel memory.

`NotepadPlusPlusLauncher` builds a `ProcessStartInfo`
using separate `ArgumentList` entries (`-nLINE`, filename)
rather than interpolating a shell command. Windows
App Paths/Program Files/PATH supply automatic discovery;
optional explicit `notepad++.exe` selection supports a
portable installation without persistent settings.
No editor is started in CI. WPF smoke tests verify
automatic filtering through real checkboxes, all/none
recalculation, deduplicated App/component evidence after
selection, safe path containment, and Notepad++ command
construction for an original log line.

### Bounded exception continuation evidence

`LogParser` attaches contiguous recognized undated .NET exception headers,
indented frames and inner-exception markers to the preceding timestamped
`LogEvent`. The per-event capture is capped at 32 lines and 4,096 characters;
`ContinuationTruncated` records overflow while `ContinuationLines` counts
all recognized lines. Blank/unrecognized undated content terminates the
association. Severity tokens are anchored to the beginning of the
post-timestamp text, preventing false ERROR classifications in device messages.

`AnomalyDetector` propagates available exception context from its selected
representative event to `AnomalyFinding`. Burst representatives favor a
higher severity or same-severity entry with stack evidence. Timeline and rare
pattern calculations, cross-target duplicate identity, and original
file/physical-line provenance are unaffected. WPF displays a conditional
collapsed stack expander. Synthetic tests cover truncated context, boundaries,
severity, propagation, component filtering, and actual WPF binding.


### Discoverable exception evidence independent of findings

`DemoLogs` contains a synthetic, explicitly labeled multiline exception
on the existing rare FATAL event. `AnomalyViewModel.Apply` enumerates the
deduplicated, currently enabled `report.Source.Events`, counts all events
with captured continuation text, and displays at most the **40 most recent**
as `AnomalyExceptionRow` entries. This is independent of the
`AnomalyDetector` finding threshold and therefore useful even with
zero anomalies. The WPF overview shows a dedicated `ExceptionCount`
metric; its separate section has per-event expanders with bounded
original detail and a typed `OpenExceptionCommand` using the existing
validated Notepad++ path/line launcher. Demo-only data has no local
file target and cannot launch the editor. All/none/file filtering uses
the same merged selection and source-file provenance as the findings.
Tests verify the demo, non-anomalous context, WPF template, filtering,
mirrored event identity, and safe external-editor availability.

### Deterministic structured TOM_OP timeline

`OperationTimelineAnalyzer.Analyze(LogParseResult)` reads only already
imported events and decodes documented `TOM_OP v=1` fields. It maps
(session, flow) to a transaction only when explicit records containing
both IDs give an unambiguous association. It does not join records by
temporal proximity or interpret undocumented error codes. Multiple
flows may share one VTS transaction, but repeated VTS numbers across
different sessions are never merged.

`OperationTimelineReport` describes evidence-derived outcomes,
distinguishing an API acknowledgement from a physical card issued,
receipt printing success, and confirmed VTS closure. An explicit
operator cancellation makes `GenericError` an expected cancellation
commit status; without that evidence, the status is reported as
unknown cause. Preview-review duration is measured only between
documented preview and decision events. Unknown versions are ignored.

The WPF view computes the operation report off the UI dispatcher on
every successful import, filter update and reanalysis; cancellation
preserves earlier results. It displays bounded groups/rows with
typed source-file/line navigation using the existing Notepad++
launcher. No local files, private source, or model input are persisted
or transmitted.

A second exact-ID pass resolves missing `session_id` on end records
from the matching `operation_id` start record when that ID maps to
exactly one session. This matches the observed TOM_OP wire format
without ambiguous cross-session merging or timestamp heuristics.

### Large-source streaming and bounded evidence retention

`LogParser.Parse(TextReader)` parses every physical line without
buffering the complete original file. The same streamed parser is
used for standalone LOG/TXT selection and recursive Logs-folder
imports. Files above 16 MiB are ordinary inputs, not automatic
exclusions; document indexing in Smart Search is unaffected.

A 1,000,000-event in-memory budget bounds the events retained for
deduplication, grouping, timelines and reversible Include/Exclude.
If the remaining space is too small for an entire file, the parser
stops at the limit; the importer discloses that source as excluded
without retaining a partial file. The number of events in a skipped
source beyond the bound is intentionally not claimed as known.
Operations sources retain priority for correlation; admitted
sources are sorted by path afterward to preserve mirror provenance.

Synthetic engine tests import a real >16 MiB UTF-8 log with 9,000
dated entries, plus an operation trace. They also import more than
512 archived logs without exclusions. Windows WPF smoke tests
exercise both single-file and folder streaming and an intentionally
small event budget to validate partial-import warnings, preserved
state, Notepad++ physical-line links and failure behavior.

The detector also supports multi-year archive spans by expanding
the bucket interval to retain at most 720 buckets rather than
discarding the imported report due to the former 366-day guard.
The WPF chart uses date-aware labels and explicitly notes the
coarse granularity. Single-event provenance and the TOM_OP
operation timeline retain their original timestamps and source lines.
A deterministic small-budget fixture verifies whole-file exclusion,
path/line preservation, skip warnings and byte accounting; a
separate multi-year fixture verifies coarse bounded buckets.

### Consolidating repetitive log evidence without asserting causes

`AnomalyDetector` now reports the count of flagged statistical buckets
separately from consolidated finding rows. Bursts are grouped by
`NormalizePattern(representativeEvidence)` with explicit
`AffectedWindows`, `FirstWindow` and `LastWindow` metadata, while the
sample line is preserved for Notepad++ source navigation. The sum of
bucket-level WARN/ERROR/FATAL events is not the number of occurrences
of the representative message. Ranking reserves capacity for rare
errors, instead of allowing one noisy component to dominate all 24
cards.

From the same already-deduplicated `LogParseResult`, the detector
builds up to 12 `LogMessagePattern` summaries. A key consists of
`(LogSeverity, NormalizePattern(message))`; the summary tracks actual
occurrences, first/last timestamps, distinct source paths and a
recent original example with physical source line. The full pattern
count is retained even when only the top 12 rows are visible. This
pattern model does not infer hardware failure or business causality.

The WPF ViewModel also independently groups dated events carrying
multiline exception context. Its display key combines severity,
normalized dated message and normalized first continuation line.
These are only similar context signatures: stack-frame tails can
differ. `ExceptionCount` remains the original count; display rows
carry `Occurrences`, `FirstSeen` and `LastSeen`, along with an
unchanged authorized local source path/line for opening the sample.

The pattern panel uses collapsed WPF expanders; findings scroll
inside a bounded-height region. Synthetic .NET and WPF regressions
check grouping, preserved severity and file/line attribution, rare
errors not hidden by bursts, dynamic reanalysis and no access to
non-imported sources. No proprietary log text enters the repo.


### Non-transactional card reads are not missing VTS links

`OperationTimelineGroup.Kind` differentiates `VtsTransaction`,
`StandaloneCardRead` and `UnlinkedFlow`. The analyzer recognizes
`CardInfoReadAndChecks` as a standalone read/check operation when
there is no explicit transaction association and no other named
operation in that flow. WPF shows **Card reading/checks** instead
of incorrectly reporting **VTS link unknown**. Any explicit
transaction association still takes precedence.

Because the card-insertion handler is void, `result=unknown` is an
emitter placeholder for no method return value. `event=end` with
`status=completed` means the independent card-read/check handler
**completed**, with no VTS dependency. WPF hides the placeholder
and preserves timing and source line. An absent end event is not
assumed completed; explicitly logged failures remain reportable. An unknown or
mixed-operation flow is never presumed standalone solely because
its `transaction_id` is absent. Deterministic engine and WPF
tests cover the production record shape with synthetic IDs.
