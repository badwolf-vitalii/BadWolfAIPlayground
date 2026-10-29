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

`Progress<T>` callbacks are queued by WPF, so a finished run explicitly applies the final network snapshot and discards stale late callbacks. This prevents an old progress update overwriting the final comparison after `Stop` or `Reset`.

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
