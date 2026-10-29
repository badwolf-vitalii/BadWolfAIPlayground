# BadWolf AI Playground

**A local-first C#/.NET WPF playground for understanding AI through real experiments.**

The first experiment is a fully connected neural network written from scratch in C#. It learns XOR using sigmoid activations, backpropagation, and stochastic gradient descent — with no external machine-learning libraries and no API keys.

## What works in v0.1.0

- **Neural Network Lab:** interactive network visualization (2 inputs → configurable hidden layer → 1 output).
- **Real training:** adjustable epochs, hidden units, learning rate, and random seed.
- **Live feedback:** training loss chart, progress, and all four XOR predictions.
- **Control:** start, stop, reset, and continue training an existing model.
- **Offline persistence:** save and load the network's architecture, weights, and biases as JSON.
- **Reusable engine:** the UI-independent `BadWolfAIPlayground.Core` library supports arbitrary fully connected topologies.
- **Validation:** self-contained core and WPF startup smoke tests, run in Windows CI without third-party test packages.

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

### First experiment

1. Start with **4 hidden neurons**, **10,000 epochs**, **0.7 learning rate**, and **seed 42**.
2. Click **Train**. Watch the loss decrease and the output values approach the expected XOR results.
3. Click **Save model** to write a JSON model, then **Reset** and **Load model** to restore it.
4. Change the hidden-neuron count or seed to create a fresh architecture. Running Train again with unchanged settings continues from the current weights.

The learning rate uses a dot as a decimal separator. Training runs on a background thread; the **Stop** button cancels it between epochs.

## Project layout

```text
BadWolfAIPlayground.sln
src/
  BadWolfAIPlayground.Core/          # Neural network, samples, JSON model format
  BadWolfAIPlayground.App/           # WPF UI, graphs, MVVM, file dialogs
tests/
  BadWolfAIPlayground.Core.SmokeTests/ # Package-free console checks
.github/workflows/build.yml          # Windows build + smoke tests
```

Read [architecture notes](docs/architecture.md) for the math, module boundaries, limitations, and next steps.

## Verify the engine

```powershell
dotnet run --project tests/BadWolfAIPlayground.Core.SmokeTests/BadWolfAIPlayground.Core.SmokeTests.csproj --configuration Release
```

The core checks cover XOR convergence, seeded initialization, serialization, independent snapshots, multi-layer topologies, and input validation. CI also builds and starts the WPF window, verifies the four prediction cards, and checks that progress bars use OneWay bindings for read-only ViewModel properties.

On Windows you can run the WPF startup check locally:

```powershell
dotnet run --project tests/BadWolfAIPlayground.App.SmokeTests/BadWolfAIPlayground.App.SmokeTests.csproj --configuration Release
```


## Roadmap

- **v0.1:** Neural Network Lab, XOR, loss visualization, JSON persistence.
- **Later:** import custom datasets, compare experiments, additional activation functions and optimizers.
- **Later:** semantic search using local embeddings, anomaly detection for logs and time-series data.

## License

[MIT](LICENSE) © 2026 Bad Wolf.
