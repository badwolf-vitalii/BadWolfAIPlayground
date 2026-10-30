# BadWolf AI Playground

**A local-first C#/.NET WPF playground for understanding how AI can be useful in normal software.**

BadWolfAIPlayground contains small, interactive experiments you can understand, inspect, and reuse in your own applications. All labs run on the local Windows computer; no cloud API key is required.

## Experiments

### Neural Network Lab (v0.1.0)

A fully connected neural network implemented from scratch in C#, with **no external machine-learning library**. Learn XOR using sigmoid, backpropagation, and stochastic gradient descent.

- Four-step beginner guide with the XOR truth table, untrained predictions, training, and **Before / Now** results.
- Interactive visualization of weights, loss history, four predictions, progress, and accuracy.
- Adjustable hidden neurons, epochs, learning rate, and random seed; optional paced animation.
- Start, stop, reset, continue training; save/load models with versioned JSON.

### Smart Search Lab (v0.2.0)

**Real local semantic search:** import plain-text documents, compute their embeddings using the locally running EmbeddingGemma model, then compare exact-word search with ranked semantic results side-by-side.

- **Use 4 demo notes** to experiment immediately, or select local TXT, MD, LOG, JSON, CSV, XML, C#, XAML, YAML, or config files. The importer supports UTF-8, Unicode files with a BOM, and explicitly chosen legacy Windows encodings.
- Keyword matching works **without any model**. The baseline uses whole-word matches, ignores common Ukrainian/English/Italian filler words when possible, and prioritizes coverage of distinct question terms rather than matches inside unrelated words. When Ollama is available, **Build AI index** computes document embeddings and **Compare searches** embeds the question and ranks documents using cosine similarity.
- Try the built-in **Ukrainian question against English documents** to see how multilingual meaning-based retrieval differs from literal word matching.
- Shows sources, relevant text excerpts, separate lexical and semantic rankings, and the actual cosine score. It does not invent AI results if Ollama is unavailable.
- **Resumable local indexing:** each completed batch is checksummed and saved atomically on disk. The exact document contents and Ollama model digest must match before cached vectors are reused. Imports remain bounded and cancellable.
- **Restore previous files** reloads only the user-selected local paths and their chosen encoding after a restart; the files are never loaded automatically, and their text is not copied into the checkpoint cache.
- The File encoding dropdown has a high-contrast dark selection box and dark dropdown entries.
- Reusable `BadWolfAIPlayground.Search` library independent of WPF; no external NuGet packages.


### Anomaly Detection Lab (v0.3.0 development)

The third lab performs **local, explainable statistical analysis** of
timestamped application logs without Ollama, codebase access, cloud
services, or external ML dependencies.

- Open a local \`.log\` or \`.txt\` file or load the entirely synthetic
  36-minute demo with a deliberately injected error burst.
- Reuse the bounded UTF-8/Unicode importer from Smart Search Lab, but
  **never save, index, embed, or upload production log text**.
- Parse dated log entries with common ISO/.NET timestamp forms and
  \`TRACE/DEBUG/INFO/WARN/ERROR/FATAL\` levels; count skipped and
  continuation lines. Unrecognized lines do not acquire invented times.
- Aggregate WARN/ERROR/FATAL counts per interval. The baseline uses
  the median and median absolute deviation (MAD), so an exceptional
  error burst can be highlighted against the file's own typical rate.
  A tiny log has no defensible baseline and should show that limitation.
- Normalize numbers, GUIDs, and hexadecimal addresses to group error
  messages into approximate templates, highlighting rare patterns only
  with enough observations.
- Show time-bucket timeline, detected findings, exact example log lines,
  threshold/explanation, and a user-adjustable sensitivity setting.
- Support cancellation and preserve the previous valid results when
  a new import fails.

**Important:** this component does NOT understand undocumented error
codes or prove the cause of a crash. It is an unsupervised statistical
detector, not an LLM. To interpret real work logs, we should validate
the timestamp/message format on a *sanitized local excerpt* and then
add a **project-specific event glossary**. An optional locally run
Ollama interpreter may use the glossary and cite original lines,
but should never claim to know undocumented system semantics.
Confidential work logs should not be committed to a public repository.

#### Local model setup (one-time)

1. Install [Ollama](https://ollama.com/download) on Windows and start it.
2. Open PowerShell and download the multilingual [EmbeddingGemma](https://ollama.com/library/embeddinggemma) model:

   ```powershell
   ollama pull embeddinggemma
   ```

3. Launch the app, select **Smart Search Lab** on the left, and click **Check local model**.
4. Click **Use 4 demo notes**, then **Build AI index**, then **Compare searches**. Try other queries, including Italian and Ukrainian.

The model download is roughly **622 MB**. The first download requires internet access, but after installation the embeddings are computed by **your own Ollama service at `http://127.0.0.1:11434`**; no document content is sent to a third-party API. The app does not download models automatically. Ollama 0.11.10 or later is required for this model.

**Why Ollama, not ONNX Runtime yet?** This first integration isolates model inference behind `IEmbeddingProvider` and uses Ollama's local `/api/embed` endpoint, so the WPF app needs no tokenizer, ONNX model conversion, or extra NuGet dependencies. A self-hosted ONNX provider can be added later without changing the indexing/search logic. The result is actual ML-based retrieval, not a simulated AI response.

**Large text files:** the import now reads files in cancellable blocks, with a separate import progress bar. The lab accepts a **3.5 MB TXT file** without splitting it by hand. The limits are **40 files, 16 MiB per file, 32 MiB combined, 24 million decoded characters, and 25,000 search chunks**. Text is divided into overlapping **1,800-character** chunks (180-character overlap). Chunks are prepared off the UI thread, retained for subsequent queries and embedded in batches of eight via local Ollama. The UI shows the estimated number of batches before indexing and progress during indexing; **large files can take many minutes** depending on your hardware and text size. Every completed eight-chunk batch is saved to `%LOCALAPPDATA%\BadWolfAIPlayground\SearchIndexes` as a checksummed binary checkpoint. You can **Cancel** or close the app at any point: no incomplete index can be queried, but completed batches remain on disk. After restarting, open Smart Search Lab, click **Restore previous files**, and then **Build AI index**. The app reloads the files, checks their exact text and the model digest, and skips all compatible cached batches. If files, chunking or the local model change, it creates a new cache instead of reusing stale results. Fully completed indexes also load from saved vectors without re-embedding the documents. The most recent file paths and decoding choice are saved (not file contents) in `%LOCALAPPDATA%\BadWolfAIPlayground\last-search-files.json`. Models still need Ollama for query embeddings. To erase private local metadata and vector caches, delete those locations in File Explorer when the application is closed; vector data may reveal information about the documents and is not encrypted. Importing another file set replaces the previous one only on successful completion. The default **File encoding: Auto** accepts UTF-8 and Unicode with a BOM. If a text file fails to decode, the **Choose documents** card shows the actual file name and reason (rather than only a generic "Import did not complete" message). For old ANSI text, select **Windows-1251 (Cyrillic)** or **Windows-1252 (Western)** before retrying; BOM-less UTF-16 LE/BE can also be selected explicitly. The importer deliberately does not guess legacy encodings because the wrong code page can silently corrupt search results. Binary data with NUL characters is still rejected. Cosine scores are relevance rankings, not calibrated correctness probabilities.

#### Ask the document (optional local Q&A)
#### Why a document question may not return an answer

When **Ask document** cannot verify an answer, expand **Inspect source passages sent to Qwen**.
The UI now distinguishes three cases: no passages retrieved, the local model declining
to answer from the selected passages, or the model producing text with an unverified
quotation. It shows the actual bounded excerpts that were sent to the model and the
search phrases used (including model-proposed alternatives). This makes it possible to
see whether the right passage was retrieved before blaming the answer model.

The retrieval step reserves slots for **exact keyword candidates and embedding candidates
from the original question** before filling the remaining positions from the blended
ranking, avoiding loss of a rare literal match under multiple weak semantic rewrites.
The source quote check is applied to the exact excerpt supplied to Qwen rather than
hidden text elsewhere in a longer indexed chunk. A matched quote establishes source
provenance, not factual correctness. This still does not guarantee good answers;
text coverage, language and model behavior must be validated on real documents.


**Compare searches** displays nearest text snippets and similarity scores; it does not answer natural-language questions. A model may return five weakly related snippets for a question, especially when the selected file omits the relevant passage.

For **Ask document**, install a separate text-generating Ollama model once (EmbeddingGemma can only produce vectors):

```powershell
ollama pull qwen3:4b-instruct
```

The instruction model is approximately 2.5 GB. You can continue using keyword and semantic search without it. With an AI index built, type a question and click **Ask document**. The app rewrites the question into search terms, combines lexical and semantic retrieval, sends at most seven passages to the local answer model, and displays a short response with quoted evidence and source chunk IDs. The model is instructed to use **only the selected documents** and to decline when the excerpts do not support an answer. The app first checks quoted passages against the actual imported text, then checks that the quotes mention distinctive answer facts such as names and numbers. An answer without a relevant, verifiable quote is rejected.

**Context handling:** the answer model uses an explicitly requested **8,192-token context window**, with up to seven source excerpts bounded to **900 characters each** (rather than sending entire 1,800-character search chunks). Source quotes are validated against the full text regardless of excerpt truncation. This avoids the usual local 4,096-token default causing the `request exceeds available context size` error. Enlarging the context uses more model memory; if Ollama still rejects the request, check its context-length setting. Existing text files and completed embedding indexes do not need to be reimported or rebuilt for this change.

**Important:** This is a grounded local RAG experiment, not a guaranteed fact checker. A verbatim quote confirms that the text exists but does not prove the interpretation. The language model might still misread a passage. Questions about absent chapters or concepts (such as the traditional "apple" when a translation says only "fruit") may return "not found" or need different wording. No text is sent to cloud services.

#### Try your own large TXT file

1. In **Smart Search Lab**, choose **File encoding: Auto** (default) and click **Select text files** to choose your 3.5 MB TXT file. For a file previously saved in Windows-1251 or Windows-1252, select that encoding instead.
2. Watch the **Reading / Preparing searchable text chunks** indicator. If import fails, the specific reason appears directly in red in the **Choose documents** card. Select the correct encoding and retry; existing documents are preserved on failure. After import, the app displays how many chunks it created and how many local Ollama batches are needed.
3. With Ollama and `embeddinggemma` installed, click **Check local model**, then **Build AI index**. This can take several minutes. You may cancel safely; completed batches will be saved.
4. After restarting, click **Restore previous files** then **Build AI index** to reuse completed batches; the index progress reports how many chunks were loaded from disk.
5. Enter a question and click **Compare searches**. Keyword search works even before indexing; semantic search uses the completed in-memory index.

## Requirements

- Windows 10/11
- Visual Studio 2022 with **.NET desktop development** workload, or the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For **semantic** search only: locally running [Ollama](https://ollama.com/download) and `embeddinggemma` (no Ollama requirement for Neural Network Lab or plain keyword matching)

## Run

Open `BadWolfAIPlayground.sln` in Visual Studio, set **BadWolfAIPlayground.App** as the startup project, and press **F5**.

Or from PowerShell:

```powershell
dotnet restore BadWolfAIPlayground.sln
dotnet run --project src/BadWolfAIPlayground.App/BadWolfAIPlayground.App.csproj
```

### First experiment: Neural Network Lab tutorial

1. Open **START HERE / GUIDED XOR EXPERIMENT**.
2. Read the XOR truth table and baseline predictions.
3. Leave **4 hidden neurons, 10,000 epochs, learning rate 0.7, seed 42**, and click **Train now**. Use **Watch training live** if you want to see progress, or disable it for full-speed training.
4. Compare **Before / Now**, model loss, accuracy, and predictions. **Restart guide** does not delete trained weights; **Reset** does.

Learning rate uses a dot as decimal separator. Training runs on a worker thread, and the WPF view is only updated via its Dispatcher. Saving a model stores the architecture/weights/biases, not the dataset or training history.

## Project structure

```text
BadWolfAIPlayground.sln
src/
  BadWolfAIPlayground.Core/           # From-scratch neural network engine
  BadWolfAIPlayground.Search/         # Text chunking, retrieval, Ollama embedding adapter
  BadWolfAIPlayground.App/            # WPF tutorials, visualization, navigation, model dialogs
tests/
  BadWolfAIPlayground.Core.SmokeTests/   # XOR, persistence, topology, validation
  BadWolfAIPlayground.Search.SmokeTests/ # Chunking, rankings, HTTP protocol, file safety
  BadWolfAIPlayground.App.SmokeTests/    # Real WPF window, navigation, threading, lesson
.github/workflows/build.yml              # Windows build + all smoke tests
```

Read [architecture and technical notes](docs/architecture.md) for implementation details and limitations.

## Verification

```powershell
dotnet build BadWolfAIPlayground.sln --configuration Release
dotnet run --project tests/BadWolfAIPlayground.Core.SmokeTests/BadWolfAIPlayground.Core.SmokeTests.csproj --configuration Release
dotnet run --project tests/BadWolfAIPlayground.Search.SmokeTests/BadWolfAIPlayground.Search.SmokeTests.csproj --configuration Release
dotnet run --project tests/BadWolfAIPlayground.App.SmokeTests/BadWolfAIPlayground.App.SmokeTests.csproj --configuration Release
```

CI tests both search algorithms using deterministic fake embeddings and the Ollama wire protocol using a stub HTTP handler. CI **does not require Ollama**, and does **not** prove real-model ranking quality; that last step is a manual local integration test on a computer with the model installed.

## Roadmap

- **v0.1:** Neural Network Lab with XOR, interactive lesson, visual loss and weight diagrams.
- **v0.2:** Local semantic vs keyword search using a replaceable embedding provider.
- **Later:** stronger relevance reranking, answer quality evaluations, faster indexing, ONNX embedding backend, additional file importers, custom datasets, experiment comparisons.
- **Later:** log and telemetry anomaly detection with baseline comparisons.

## License

[MIT](LICENSE) © 2026 Bad Wolf.


#### Preventing garbled text from incorrect encodings

**Check the decoded text preview in Choose documents before building an AI index.**
Selecting Windows-1251 for a UTF-8 Ukrainian book turns normal text into
mojibake such as `РџРµС...`. Since most legacy codepages accept arbitrary
bytes, decoding may "succeed" while producing unusable text. The importer
now rejects a legacy codepage selection if a sampled file is convincingly
valid UTF-8, and also rejects text already strongly marked by common
UTF-8/ANSI mojibake patterns. It names the file and suggests
**File encoding: Auto (UTF-8)**. Previously indexed garbage cannot be
repaired from embeddings: reimport the original file with the correct
encoding and **Build AI index** again. The cache fingerprint detects
the changed source text and does not reuse the corrupted vectors.

**Faster document answers:** by default **Ask document** uses semantic
and lexical retrieval with the original question and calls the Qwen text
model only once. An optional **Deep query expansion (slower)** checkbox
restores the extra Qwen rewrite call if needed for harder searches.
Timing still depends on whether Ollama runs Qwen on GPU or CPU; check
`ollama ps` while answering (the `PROCESSOR` column should ideally be
`100% GPU`). This speedup does not guarantee relevance or answer quality.


### Local model switching and performance (CPU/GPU)

Smart Search Lab can run entirely on a **CPU-only Windows PC**. After
building the EmbeddingGemma search index, click **Refresh installed models**
under **Ask document**. Ollama's local `GET /api/tags` endpoint lists models
that are actually installed; select any supported text-generating model in
the dark-themed dropdown. **Changing the answer model does not rebuild or
invalidate the EmbeddingGemma index**, because document vectors use a
different model. Missing models are never downloaded automatically.

Smaller instruct models can reduce CPU latency (with a likely trade-off in
answer quality, especially for strict JSON citations or Ukrainian):

```powershell
ollama pull qwen2.5:0.5b-instruct  # around 398 MB; lightweight, lower accuracy
ollama pull qwen2.5:1.5b-instruct  # around 986 MB; stronger, still CPU-friendly
```

The previously used `qwen3:4b-instruct` remains the default. You can
switch back at any time. Click **Refresh installed models** after a pull.

The new **Request performance** panel shows:
- Retrieval/search time (including any optional deep query rewrite);
- Answer-generation time and end-to-end request time;
- Prompt/output token counts and Qwen output tokens per second;
- Model loading time, when reported by Ollama;
- CPU/GPU placement taken from the local `GET /api/ps` endpoint.

The CPU/GPU indicator is **unknown** if the selected model is not loaded
or Ollama does not provide usable VRAM metrics. For running models,
0% GPU memory means 100% CPU; partial offload is estimated from
`size_vram / size`. This is not a Windows GPU utilization monitor.
Output speed is computed from `eval_count / eval_duration`, not
end-to-end seconds; missing metrics are shown as *n/a*, not invented.
Both `/api/tags` and `/api/ps` are local. No API key or cloud access is
required. Low-memory models may give poorer answers; verify their
quotations instead of assuming that faster means better.


### Adaptive document answers on slower CPUs

**Ask document** now starts with **up to three source excerpts (480 characters each)**
rather than always sending all seven candidates (900 characters each) to Qwen.
The initial selection includes exact keyword and original-question semantic
candidates plus one blended candidate where available. If Qwen declines
to answer or its source quotations cannot be verified, the app **retries
once** with up to seven wider excerpts (700 characters each), keeping
stable source IDs and the same verified-quote requirement. If there are no
additional passages or source characters to supply, no redundant retry is
sent; even one long excerpt can be expanded when it contains more text.
The user can cancel at
any time. Optional *Deep query expansion* still performs additional
query-rewriting work before answering.

**Request performance** now shows the number of answer attempts and
aggregates Ollama's actual prompt and output token counts, including
both attempts (and any optional query rewrite). The token rate uses the
total output tokens divided by total measured generation duration.
The **Adaptive source context** line shows exactly how many excerpt
characters were sent and how many would have been sent using the old
full seven-passage context. It **does not claim to measure token or
wall-time savings**: only a repeatable on-device comparison can establish
those. A fallback may be slower than the old one-shot approach,
particularly for small CPU models. The evidence inspector shows the
exact excerpts used in the **final** answer attempt. An answer with
a fabricated source quote is still rejected, regardless of speed.

The previous on-device baseline for a Qwen2.5 1.5B question about
*Farbovanyi Lys* was about 35 seconds and 3,651 prompt tokens; this
is an observation from one run, not a guaranteed benchmark. Repeat
the same question with the same model to measure your real improvement.


### Citation relevance: verified text is not verified reasoning

A real user test revealed a dangerous false positive: the model answered
**"Лиса Микита"** but cited an unrelated sentence
**"Лиса мало хто й запримітив, і нікому до нього не було діла."**
The quotation was genuinely in the book but did not name the fox.

Document Q&A now performs two separate checks **without an extra AI call**:
1. Confirm that each quote is a verbatim substring of the exact text
   sent to the answer model (existing source-provenance check).
2. Check that the cited text actually contains answer-specific information:
   the key words that differ from the question, and all distinctive proper
   names or numbers present in the answer. Conservative matching accepts
   some common Ukrainian grammatical endings (e.g. Микита / Микитою).
   Irrelevant citations are removed even when other citations are valid.

When the model quotes a real but irrelevant sentence, **Ask document**
automatically attempts one wider retrieval/generation pass, as with other
uncited responses. If the wider pass still fails, the UI explicitly
reports **Unsubstantiated answer**, shows which answer words were not
found in its quotes, and does *not* show the proposed answer as verified.
The evidence inspector still shows the source text used by the model.

**Limitation:** this is a conservative *lexical* relevance check, not a
general semantic entailment engine or a factual guarantee. It can reject
a correct paraphrase when the supporting quote uses different vocabulary,
and it cannot detect every misleading quote containing the right name.
The tests reproduce the actual fox/name failure and verify that an
irrelevant citation never passes just because it is verbatim.


### Fast source-citation repair after a CPU regression

A real user test showed that the previous name-citation change caused a
**47.05-second** unsuccessful result on a CPU-only PC (4,166 input
tokens, two Qwen calls). The model answered with a name but copied a
genuine sentence that did not contain the name. Repeating Qwen with
seven excerpts did not help.

The default behavior now **avoids a second Qwen generation for a
verbatim but irrelevant citation**. Instead, it performs bounded,
deterministic search over the already loaded TXT: the originally
submitted source chunks plus up to 24 keyword-ranked local chunks
matching the answer-specific words. It extracts a short authentic
sentence containing the answer's distinctive words and at least one
topical word related to the question. It still applies name, year
and word-level corroboration. Such citations are explicitly labeled
**Located in original document** because they are sourced by the
application rather than chosen by Qwen.

This local repair is permitted only after Qwen has supplied at least
one genuinely copied quotation. Fabricated quotes are never rescued
into verified answers. If a model suggests an answer absent from the
selected source text, the application rejects it immediately without
another costly answer generation. The bounded second-generation pass
remains for true model refusals or missing/unverifiable quotations.
All work happens offline using the imported text; no second embedding
inference is needed.

This is *lexical* corroboration, NOT full semantic fact-checking or
proof that a matching proper name belongs to the right individual.
The tests reproduce the unrelated fox citation, name recovery in the
same and later chunks, an unsupported answer and an unrelated
same-name mention. Measure any real CPU improvement on the same
local book/model; correctness and throughput are not guaranteed by CI.


### Source quote ordering: prefer an introduction on equal relevance

For the question **"Як звали лиса?"**, *Farbovanyi Lys* introduces the
hero in its very first sentence: **"Жив собі в однім лісі Лис Микита..."**
The local evidence locator previously preferred a later quote from
chunk 2 because submitted Qwen passages received an extra ranking
bonus. The quote was genuine, but the introductory sentence was just
as relevant and easier for a reader to verify.

Local quote selection now ranks first by textual evidence relevance
(question-term and distinctive answer-term coverage). **When scores
are equal, it uses the original chunk and sentence order**, not which
excerpt happened to be sent to Qwen. It also considers the earliest
chunk mentioning the answer even if its one occurrence is crowded out
of the top-24 keyword hits by many later repeated names. More
specific later evidence may still win. This is a deterministic local
change; it does not trigger additional Qwen calls or invalidate
existing embeddings. Automated regression tests cover equal-scoring
introductory and later quotes, a crowded long book, and a more
relevant later sentence.


#### Optional local source-code context for application logs

You do **not** need to know the codebase to use the anomaly detector.
After opening a log, optionally click **Select local C# solution**
and choose a `.sln` file from a local checkout of a project.
The app scans only the local `.cs` files below that solution folder
(up to 4,000 files, with bounded memory/size), never downloads or
publishes the repository, and indexes simple logging statements
such as `logger.Error("...")`, `logger.Debug($"...{id}...")`
and `Helper.SimpleLogE(logger, "...")`.
When a finding's actual message matches a literal or interpolated
logging template, its card displays **Possible log origin:
relative/path/File.cs:line**. These are helpful call-site candidates,
not proof that a specific code path ran. Complex expressions and
logger wrappers may be unmatched.

Some applications also emit a DEBUG message with the caller's
`File.cs: line 123` immediately before an error; the lab recognizes
this as a nearby caller hint (without claiming it caused the error).
Source context is optional, in-memory, and never used to infer
unknown business rules or generate an unsupported diagnosis.
This generic scanner contains **no company source, logging strings,
secrets or production logs**. Always keep private source in a private
checkout, and never commit your source data to this public repo.


### Analyze a complete Logs folder (v0.3.0)

Select **Logs folder** inside Anomaly Detection Lab to discover
`.log` and `.txt` files from that folder and any subfolders,
including `old/` and dated component archives, without selecting
files one by one. The app imports as many as its bounded resource
budgets allow, merges selected events by timestamp, and displays
a single anomaly timeline, and includes a collapsible per-file /
component breakdown with per-file event and problem counts.

Each finding identifies its **original relative filename and
physical source line**. When NLog emits the exact same
timestamp/severity/message to two different targets (e.g. a general
App log and a component log), the aggregate removes the duplicate
while preserving both filenames as provenance. Distinct repeated
messages in the same file are kept. Raw per-file counts include
copies; aggregate counts exclude these exact cross-file copies.

Folder import is **offline, cancellable and non-persistent**. The
anomaly parser now reads UTF-8 / BOM-marked UTF-16 logs incrementally
through a TextReader rather than loading the whole file as a string.
**Anomaly Detection Lab no longer has a 16 MiB per-file limit,
a 256 MiB combined byte limit, or a 512-file analysis cutoff.**
Those older byte restrictions still apply separately to text indexing
inside Smart Search Lab, not log analysis.

The remaining limits concern *retained in-memory evidence*, not input
file size: scan at most **50,000 file metadata entries** and retain
at most **1,000,000 dated events** in a report. Source priority is
Operations logs, current/root components, other components, then
archived old/ logs, with recent files preferred within a class.
Files that cannot fit wholly in the remaining event budget are
explicitly listed as **SKIPPED FILES — NOT ANALYZED**, without
misrepresenting a partial transaction as a complete source.
Raw event totals and per-file provenance are based only on admitted
files. If parsing reached the limit mid-file, the skip reason reports
a lower bound, not a fabricated full event count.

Strict decoding, binary-data checks, a maximum individual line length,
bounded stack context, source-file/line navigation, cancellation and
mirror deduplication remain. Failed/canceled imports preserve the
last successful report. No work logs are uploaded or stored by this lab.
For multi-year archives, statistics use coarser time buckets while
individual structured operations retain their original timestamps.

**Deduplication safeguard:** two unrelated components are *not*
assumed to be copies simply because they emitted identical text at
the same timestamp. Automatic merging is restricted to the known
generic App/component mirroring pattern and duplicate copies of
the same named file from different directories. Other
identical events remain visible separately. This is tested with
two synthetic independent device logs.


### Enable/disable individual log files and jump to Notepad++

After selecting a whole Logs folder, expand **Files & Components**:
- Each imported file has an **Include** checkbox. Changes
  automatically rerun detection on **only the enabled files**,
  updating the summary, error timeline, finding cards and per-finding
  evidence without re-reading files from disk.
- **Include all** and **Exclude all** provide quick selection.
  An empty selection displays zero analyzed events rather than a
  misleading all-components baseline. Per-file row counts remain
  visible for comparison, regardless of the Include state.
- The imported original per-file events are retained in memory
  solely for this session. When files are toggled, exact
  mirrored-event deduplication is recomputed from the enabled
  source files. Thus turning off a component log will not
  accidentally erase an event that remains in its enabled
  App log (or vice versa). Cross-component duplicates never
  cause unrelated logs to be discarded.

Every finding now has an **Open in Notepad++** button that opens the
original on-disk `.log`/`.txt` file at its **actual physical
source line** (`notepad++.exe -n42 "C:\\path\\file.log"`).
The per-file list offers an **Open** button (line 1).
Notepad++ is discovered from Windows App Paths, standard
installation folders, or PATH. For portable installations,
click **Locate Notepad++** and select `notepad++.exe` manually.
No Notepad++ installation or model download is performed by
the application. When the editor is missing or the file
has moved, a clear error is displayed without changing the
analysis. For synthetic demo data, opening a nonexistent file
is disabled.

The tool stores the selected folder's absolute location
**only in process memory** to resolve original files; no local
work paths, event content or editor settings are committed,
transmitted, or persisted. Source file paths are validated
against the imported file list and the selected root before
opening to prevent traversal outside the Logs directory.
Opening a log is a read/view action in an external editor;
saving changes in that editor is the user's responsibility.

### Multiline .NET exception and stack context

For NLog layouts that emit exception stack traces on undated lines, finding cards
now show a collapsed **Exception / stack trace (original log)** panel when
there is captured context. Contiguous exception headings, stack frames, and
inner-exception markers are attached to their preceding dated event. An
unrelated or blank undated line ends the association; no timestamp or
severity is guessed.

Retained evidence is limited to **32 lines and 4,096 characters per event**.
Additional recognized stack lines are counted but omitted with a visible
truncation notice. Duplicate-event matching still uses timestamp, explicit
severity and primary message, not the continuation. A severity-looking word
inside a device message is not treated as a log level. Captured details
remain in memory and do not imply a proven root cause.


**Where to see it:** Open **Anomaly Detection Lab** and click **Use demo log**.
The demo now contains a synthetic `System.IO.IOException` stack trace.
The **Analysis overview** includes a visible **STACK CONTEXT** count,
and section **4 — Exception / stack trace details** lists the source
timestamp, original file/line, severity and event message. Expand a row
to read the bounded exception stack. The list works **even when there
are zero statistical findings**, and updates as files are included
or excluded. The newest **40** entries are shown to keep the UI
responsive; the counter reports the complete count for enabled logs.
For real local logs, the expanded entry can jump to its original
line in Notepad++. The button is disabled for the synthetic demo.

A count of zero means no *multiline* context was captured; an exception
written entirely on the original dated line remains in that message
and is not counted as a separate stack trace. No exception content
is sent, persisted or embedded.

### Structured operation lifecycle timeline (TOM_OP v1)

In **Anomaly Detection Lab**, choose a local Logs folder (including nested old/)
or an individual Operations log. The new **Operation lifecycle** section
shows transaction groups with their original log file/line, timings, user
actions, dialog responses and completion outcomes. It works on INFO-only
logs as well as warnings and errors, with no model, network access or cloud.

The analyzer joins only explicit `session_id`, `transaction_id` and
`flow_id`. Pre-transaction events join the transaction only if later
evidence explicitly links the same session and flow to exactly one VTS ID.
Unlinked flows remain separate; the same numeric VTS ID in another
session is a different group. The newest 40 groups and 80 events per
group are displayed with complete group/event counts and safe
Notepad++ source navigation. File Include/Exclude filters apply here too.

A successful `VtsTransactionClose` is distinct from a failed
`PrintReceipt`. A `card_issuing_cancelled reason=user` event establishes
intentional operator cancellation, for which `GenericError` can be
the expected VTS commit status, not a hardware diagnosis. Watchdog and
programmatic exits remain separate. Preview-to-decision time is
reported only when explicit `card_preview_ready` and decision events
exist, never guessed from an unexplained timestamp gap.

No proprietary logs are committed. All deterministic fixtures are
synthetic, and imported data stays local/in memory.

The emitter may omit `session_id` on an operation's `event=end`
record. The analyzer resolves the missing session only through a
matching, unique `operation_id` from an explicitly session-scoped
start/step event. End records with no unique match are kept separate;
time proximity is never treated as proof.

#### Large LOG/TXT files use streaming

A `WebClientProfiler_2026-10-30.log` of 35.7 MiB no longer gets
excluded simply because it exceeds 16 MiB. Both **Select Logs folder**
and **Select local LOG / TXT** parse file content line by line, retaining
bounded dated events rather than accumulating the entire source text.
Original physical line numbers remain available for Notepad++.

**SKIPPED FILES** now means a real retained-event budget was exceeded.
The list identifies affected sources without claiming they were
analyzed. Choose a more focused folder/date range to analyze excluded
files independently. Input is never silently truncated or uploaded.
Invalid encodings and binary data still produce explicit errors.

### Grouped anomaly evidence and recurring printer messages

Anomaly Detection Lab now makes an important distinction between an
individual WARN/ERROR/FATAL log event, a statistical **burst window**,
and an actual diagnosed incident. A single repetitive `Purge() FAILED`
message can produce many flagged time windows; these must not be called
independent root causes.

**Consolidated findings** group statistically flagged windows that have
the same normalized *representative* log message. Each group shows the
number of flagged windows, their first and last times, peak WARN/ERROR
rate, and the original source file and line of the strongest example.
Total problem events across these windows are clearly **not** presented
as counts of that exact representative message. Some of the 24 display
slots remain available for distinct rare-error patterns instead of
being completely consumed by frequent printer cleanup messages.
Findings scroll within a bounded-height area.

The compact **Recurring warning / error patterns** panel independently
counts actual recognized warning/error/fatal messages in the selected,
deduplicated logs. It groups by explicit severity and normalized message
(variable numbers and IDs are replaced), displays the top 12 by
occurrence count, their first/last timestamps, number of source files,
and offers an **Open example in Notepad++** action pointing to an exact
original line. Distinct normalized patterns are **not automatically
distinct hardware faults or root causes**.

The **Exception / stack trace details** section reports the unchanged
total event count but groups visually similar contexts by severity,
normalized dated message and first continuation line. Each item shows
the number of similarly formatted events, date range and latest
original example. Later stack frames may differ, so these groups do
not claim the entire exceptions are identical.

Grouping is deterministic, entirely local, and recomputed after file
Include/Exclude changes. No ignored profiler or otherwise excluded
log source is counted as evidence. Real work logs and private code
remain on the user's machine.


#### Standalone card reads versus VTS transactions

The structured `TOM_OP v=1` operation `CardInfoReadAndChecks` is a
standalone card read/check flow. `transaction_id=-` is expected
and does not indicate a missing or failed VTS transaction.
Operation lifecycle explicitly labels this known independent flow
**Card reading/checks**, rather than **Unlinked flow**.

Because `CardInsertedCore` / `CardInfoReadAndChecks` is void, the emitter's
`result=unknown` is simply a **no-return-value placeholder**, not a
failure or an uncertain card read. For `event=end status=completed`,
the analyzer reports **Card reading/checks completed**, suppressing
that placeholder in the event details. Duration, `flow_id`, source and
physical line remain visible. Explicit logged failures are still
reported, while a missing end event never implies completion.

Other unrecognized flow types remain **Unlinked flow** until their
purpose is supported by evidence. A flow with other named operations
is never automatically classified as an independent card read.
