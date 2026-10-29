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
