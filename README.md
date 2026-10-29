# BadWolf AI Playground

**A local-first C#/.NET WPF playground for understanding AI through real experiments.**

The first experiment is a fully connected neural network written from scratch in C#. It learns XOR using sigmoid activations, backpropagation, and stochastic gradient descent — with no external machine-learning libraries and no API keys. **A four-step interactive tutorial explains what the network is doing before you have to understand any of the controls.**

## What works in v0.1.0

- **Neural Network Lab:** four-step beginner guide with an XOR truth table, starting guesses, an in-guide Train button, and explanations of the outcome.
- **Interactive architecture:** network visualization (2 inputs → configurable hidden layer → 1 output).
- **Real training:** adjustable epochs, hidden units, learning rate, and random seed.
- **Live feedback:** training loss chart, progress, and four XOR prediction cards with **before vs. now** values and expected answers.
- **Control:** start, stop, reset, and continue training an existing model.
- **Offline persistence:** save and load the network's architecture, weights, and biases as JSON.
- **Reusable engine:** the UI-independent `BadWolfAIPlayground.Core` library supports arbitrary fully connected topologies.
- **Validation:** self-contained core and WPF startup/tutorial smoke tests, run on Windows CI without third-party test packages.

Smart Search and Anomaly Detection are **planned** experiments, not implemented features yet.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or Visual Studio 2022 with the .NET desktop development workload)

## Run

Open `BadWolfAIPlayground.sln` in Visual Studio, set **BadWolfAIPlayground.App** as the startup project, and press **F5**.

Or from PowerShell:

```powershell
dotnet restore BadWolfAIPlayground.sln
dotnet run --project src/BadWolfAIPlayground.App/BadWolfAIPlayground.App.csproj
```

### First experiment: follow the built-in lesson

Open the **START HERE / GUIDED XOR EXPERIMENT** panel at the top of the app:

1. **Understand the four examples.** `0 XOR 0 = 0`, `0 XOR 1 = 1`, `1 XOR 0 = 1`, `1 XOR 1 = 0`. No coding or knowledge of AI is needed.
2. **Look at the starting guesses.** The panel records the untrained network's accuracy and loss. The four cards below also show the exact **Before** prediction for each example.
3. **Train.** Leave the initial settings at **4 hidden neurons, 10,000 epochs, learning rate 0.7, seed 42**, then click **Train now** inside the guide. Watch the weights, loss, and predictions change. **Watch training live** is enabled by default to make the animation visible; disable it for full-speed training. Stop keeps the trained weights.
4. **Compare before and after.** The app shows real starting and current accuracy and the percentage change in training error. It does not invent results or claim that XOR training makes an intelligent assistant.

**Next step** unlocks the results page only after at least one epoch has trained. **Restart guide** revisits the explanations without deleting what the network learned; **Reset** creates fresh random weights and a new baseline. **Save model / Load model** store and restore the model as JSON; loading starts a new comparison baseline based on the loaded model.

For each setting the UI shows a short explanation. The learning rate uses a dot as a decimal separator. Training runs on a background thread; the **Stop** button cancels it between epochs. Changing the hidden-neuron count or seed creates a new network and clears the comparison when training starts; otherwise training continues from the existing weights.

## Project layout

```text
BadWolfAIPlayground.sln
src/
  BadWolfAIPlayground.Core/          # Neural network, samples, JSON model format
  BadWolfAIPlayground.App/           # WPF UI, graphs, MVVM, file dialogs
tests/
  BadWolfAIPlayground.Core.SmokeTests/ # Package-free core checks
  BadWolfAIPlayground.App.SmokeTests/  # Real WPF startup and tutorial checks
.github/workflows/build.yml          # Windows build + smoke tests
```

Read [architecture notes](docs/architecture.md) for the math, module boundaries, limitations, and next steps.

## Verify the engine

```powershell
dotnet run --project tests/BadWolfAIPlayground.Core.SmokeTests/BadWolfAIPlayground.Core.SmokeTests.csproj --configuration Release
```

The core tests cover XOR convergence, seeded initialization, serialization, independent snapshots, multi-layer topologies, and input validation. CI also builds and **launches the real WPF window**, checks read-only progress bindings, walks the guided steps, executes a short training run, and verifies baseline/reset behavior.

On Windows, run the UI check locally:

```powershell
dotnet run --project tests/BadWolfAIPlayground.App.SmokeTests/BadWolfAIPlayground.App.SmokeTests.csproj --configuration Release
```

## Roadmap

- **v0.1:** Neural Network Lab, XOR, loss visualization, JSON persistence.
- **Later:** import custom datasets, compare experiments, additional activation functions and optimizers.
- **Later:** semantic search using local embeddings, anomaly detection for logs and time-series data.

## License

[MIT](LICENSE) © 2026 Bad Wolf.
