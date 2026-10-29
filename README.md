# BadWolf AI Playground

**A local-first C#/.NET WPF playground for understanding how AI can be useful in normal software.**

BadWolfAIPlayground contains small, interactive experiments you can understand, inspect, and reuse in your own applications. Both existing labs run on the local Windows computer; no cloud API key is required.

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
- Keyword matching works **without any model**. When Ollama is available, **Build AI index** computes document embeddings and **Compare searches** embeds the question and ranks documents using cosine similarity.
- Try the built-in **Ukrainian question against English documents** to see how multilingual meaning-based retrieval differs from literal word matching.
- Shows sources, relevant text excerpts, separate lexical and semantic rankings, and the actual cosine score. It does not invent AI results if Ollama is unavailable.
- In-memory search index, cancellation, indexing progress, input size limits, and helpful setup/status messages.
- Reusable `BadWolfAIPlayground.Search` library independent of WPF; no external NuGet packages.

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

**Large text files:** the import now reads files in cancellable blocks, with a separate import progress bar. The lab accepts a **3.5 MB TXT file** without splitting it by hand. The limits are **40 files, 16 MiB per file, 32 MiB combined, 24 million decoded characters, and 25,000 search chunks**. Text is divided into overlapping **1,800-character** chunks (180-character overlap). Chunks are prepared off the UI thread, retained for subsequent queries and embedded in batches of eight via local Ollama. The UI shows the estimated number of batches before indexing and progress during indexing; **large files can take many minutes** depending on your hardware and text size. You can **Cancel** at any point. An interrupted index is discarded (no partial results), while loaded files are retained; the next Build starts from the beginning. All data and the completed index are currently held in memory, so imports and indexes must be recreated after restarting the app. Importing another file set replaces the previous one only on successful completion. The default **File encoding: Auto** accepts UTF-8 and Unicode with a BOM. If a text file fails to decode, the **Choose documents** card shows the actual file name and reason (rather than only a generic "Import did not complete" message). For old ANSI text, select **Windows-1251 (Cyrillic)** or **Windows-1252 (Western)** before retrying; BOM-less UTF-16 LE/BE can also be selected explicitly. The importer deliberately does not guess legacy encodings because the wrong code page can silently corrupt search results. Binary data with NUL characters is still rejected. Cosine scores are relevance rankings, not calibrated correctness probabilities.

#### Try your own large TXT file

1. In **Smart Search Lab**, choose **File encoding: Auto** (default) and click **Select text files** to choose your 3.5 MB TXT file. For a file previously saved in Windows-1251 or Windows-1252, select that encoding instead.
2. Watch the **Reading / Preparing searchable text chunks** indicator. If import fails, the specific reason appears directly in red in the **Choose documents** card. Select the correct encoding and retry; existing documents are preserved on failure. After import, the app displays how many chunks it created and how many local Ollama batches are needed.
3. With Ollama and `embeddinggemma` installed, click **Check local model**, then **Build AI index**. This can take several minutes. You may cancel safely; the original imported text remains in the lab.
4. Enter a question and click **Compare searches**. Keyword search works even before indexing; semantic search uses your newly built index. After a restart, rebuild the in-memory index.

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
- **Later:** saving/loading search indexes, ONNX embedding backend, additional file importers, custom datasets, experiment comparisons.
- **Later:** log and telemetry anomaly detection with baseline comparisons.

## License

[MIT](LICENSE) © 2026 Bad Wolf.
