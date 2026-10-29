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

- **Search library (`net8.0`):** `LocalTextLoader` imports bounded UTF-8 / BOM-marked UTF-16 files in cancellable ~32K-character reads with byte-based import progress, `TextChunker` splits documents into 1,800-character windows with 180-character overlap, `KeywordSearch` counts literal query-term occurrences with cancellation, and `SemanticSearch` builds a normalized vector index and computes cosine similarity. The public `IEmbeddingProvider` interface keeps model inference interchangeable.
- **Local model adapter:** `OllamaEmbeddingProvider` sends only to local loopback HTTP (`127.0.0.1:11434/api/embed`) by default, using EmbeddingGemma. It posts arrays of inputs in batches of eight and validates counts, vector sizes, finite values, and nonzero norms. The UI exposes an explicit connection check and a user-copied model installation command. It never downloads or uploads files on its own.
- **Retrieval prompts:** for documents, `title: {document name} | text: {chunk}`; for queries, `task: search result | query: {query}`. The model supports multilingual text; cross-language quality depends on model/data and is not guaranteed.
- **UI:** `SmartSearchView` is a second navigable lab within `MainWindow`, with its own `SmartSearchViewModel`. It exposes sample text, file import, AI index creation with progress/cancellation, and side-by-side raw lexical versus semantic results. The lab works in a keyword-only mode if Ollama is unavailable, without presenting fabricated semantic results.
- **Index lifecycle:** indexes are intentionally in memory and become invalid when the document set changes. They carry the embedding model name so query embeddings cannot accidentally be compared to vectors from a different model. Document import and text splitting are cancellable and publish the new set atomically, preserving the previous set on failure. Parsed chunks are cached, avoiding repeated split work on the UI thread; keyword scanning runs on a worker. Indexing checks cancellation between embedding batches; a completed index is published atomically and incomplete vectors are discarded. One index is bounded to 25,000 chunks (up to 16 MiB per input file, 32 MiB total, 24 million characters). The UI displays the approximate number of eight-chunk batches and per-batch progress. Building a large index can take minutes and requires free RAM for the document text, chunks, and vectors.
- **Scoring:** lexical results are ranked by term occurrences; semantic results use normalized dot product, equal to cosine similarity. A value like `0.71` is **not a 71% probability**. Review the retrieved source before trusting any result.
- **Security and limitations:** only user-selected local text files are read; text import enforces a file count and size ceiling plus valid UTF-8. Search does not execute file content or follow internet links. A model's local endpoint may write its own logs/cache as configured by Ollama. No persisted index, OCR, PDF import, or background file watching is included.
- **Verification:** package-free search smoke tests use a deterministic mock embedding provider, an HTTP handler stub for Ollama, chunk boundaries, multi-megabyte UTF-8 import, byte-based progress, safety limits, batch indexing, cancellation, cross-language ranking behavior, and validation. The Windows WPF smoke test navigates between labs, verifies keyword search with Ollama absent, and imports a synthetic 3.5 MiB file to catch regressions in UI responsiveness and file limits. Actual embedding model performance still requires a manual local test with EmbeddingGemma installed.
