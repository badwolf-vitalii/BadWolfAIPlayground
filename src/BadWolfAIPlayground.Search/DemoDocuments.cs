namespace BadWolfAIPlayground.Search;

public static class DemoDocuments
{
    public static IReadOnlyList<SearchDocument> Create() =>
    [
        new("database_errors.txt", """
            A SQLite write transaction failed with SQLITE_BUSY: database is locked.
            This can happen when another connection holds a long-running write transaction.
            Diagnose concurrent readers and writers, check transaction boundaries, and configure busy_timeout.
            Never assume that retrying without a limit will fix database contention.
            """),
        new("wpf_crash_notes.txt", """
            The WPF application crashes during startup with an InvalidOperationException.
            A TwoWay binding cannot write to a read-only ViewModel property such as Completion.
            Bind ProgressBar.Value using Mode=OneWay and test the actual window at startup.
            """),
        new("gpu_driver_incident.txt", """
            Windows recorded VIDEO_TDR_FAILURE, bugcheck 0x116, after both monitors went black.
            GPU fans accelerated and the display did not recover. Investigate graphics drivers,
            the video card, PCIe power delivery, and relevant Windows crash dump evidence.
            """),
        new("quiz_design_notes.txt", """
            An XOR neural network learns from four examples. Its output is one when the two input bits differ.
            The training process updates weights by backpropagation. A lower loss suggests better predictions
            on the training examples, but does not by itself prove real-world generalization.
            """)
    ];
}
