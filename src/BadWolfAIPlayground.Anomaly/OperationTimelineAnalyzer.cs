using System.Globalization;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>
/// Interprets documented TOM_OP v=1 lifecycle records without inferring
/// undocumented business behavior from nearby timestamps or error words.
/// All input is already in memory, and source locations are preserved.
/// </summary>
public static class OperationTimelineAnalyzer
{
    public const int MaxDisplayedGroups = 40;
    public const int MaxDisplayedEventsPerGroup = 80;

    public static OperationTimelineReport Analyze(
        LogParseResult source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var events = new List<OperationTimelineEvent>();
        for (int i = 0; i < source.Events.Count; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            OperationTimelineEvent? item = Read(source.Events[i]);
            if (item is not null)
                events.Add(item);
        }

        // Lifecycle end records do not always repeat session_id or process_id.
        // Resolve their session only through the exact matching operation_id
        // from a start/step record. Never derive it from a nearby timestamp.
        var operationSessions = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        foreach (OperationTimelineEvent item in events)
        {
            if (!Known(item.OperationId) || !Known(item.SessionId))
                continue;
            if (!operationSessions.TryGetValue(item.OperationId,
                    out HashSet<string>? sessions))
                operationSessions[item.OperationId] =
                    sessions = new HashSet<string>(StringComparer.Ordinal);
            sessions.Add(item.SessionId);
        }
        for (int i = 0; i < events.Count; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            OperationTimelineEvent item = events[i];
            if (!Known(item.SessionId) && Known(item.OperationId) &&
                operationSessions.TryGetValue(item.OperationId,
                    out HashSet<string>? sessions) && sessions.Count == 1)
            {
                events[i] = item with { SessionId = sessions.First() };
            }
        }

        // Correlate early, pre-transaction UI work only when a real
        // session+flow is explicitly present alongside a VTS ID later.
        // An ambiguous flow is never guessed from time or event order.
        var flowTransactions = new Dictionary<(string Session, string Flow),
            HashSet<string>>();
        foreach (OperationTimelineEvent item in events)
        {
            if (item.SessionId.Length == 0 || !Known(item.FlowId) ||
                !Known(item.TransactionId))
                continue;

            var key = (item.SessionId, item.FlowId);
            if (!flowTransactions.TryGetValue(key, out HashSet<string>? values))
                flowTransactions[key] = values = new HashSet<string>(StringComparer.Ordinal);
            values.Add(item.TransactionId);
        }

        var groups = new Dictionary<(string Scope, string Id),
            List<OperationTimelineEvent>>();
        var transactions = new Dictionary<(string Scope, string Id), string>();
        int unlinked = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            OperationTimelineEvent item = events[i];
            string scope = item.SessionId.Length != 0 ? "session:" + item.SessionId :
                Known(item.ProcessId) ? "process:" + item.ProcessId :
                "file:" + item.SourceFile;
            string transaction = Known(item.TransactionId) ? item.TransactionId : "";
            if (transaction.Length == 0 && item.SessionId.Length != 0 &&
                Known(item.FlowId) &&
                flowTransactions.TryGetValue((item.SessionId, item.FlowId),
                    out HashSet<string>? associated) && associated.Count == 1)
            {
                transaction = associated.First();
            }

            string id = transaction.Length != 0 ? "transaction:" + transaction :
                Known(item.FlowId) ? "flow:" + item.FlowId : "";
            if (id.Length == 0)
            {
                unlinked++;
                continue;
            }

            var key = (scope, id);
            if (!groups.TryGetValue(key, out List<OperationTimelineEvent>? list))
                groups[key] = list = [];
            list.Add(item);
            if (transaction.Length != 0)
                transactions[key] = transaction;
        }

        var results = new List<OperationTimelineGroup>();
        foreach (var pair in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<OperationTimelineEvent> items = pair.Value
                .OrderBy(e => e.Timestamp).ThenBy(e => e.SourceFile,
                    StringComparer.OrdinalIgnoreCase).ThenBy(e => e.SourceLine).ToList();
            string transaction = transactions.GetValueOrDefault(pair.Key) ?? "";
            string[] flowIds = items.Where(e => Known(e.FlowId))
                .Select(e => e.FlowId).Distinct(StringComparer.Ordinal).ToArray();
            (string outcome, string reason) = Classify(items, transaction);
            results.Add(new OperationTimelineGroup(
                items[0].SessionId, transaction, flowIds,
                items[0].Timestamp, items[^1].Timestamp, outcome, reason,
                items.Count, items.TakeLast(MaxDisplayedEventsPerGroup).ToArray()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new OperationTimelineReport(
            events.Count, unlinked, results.Count,
            results.OrderByDescending(g => g.EndedAt)
                .Take(MaxDisplayedGroups).ToArray());
    }

    private static (string Outcome, string Explanation) Classify(
        IReadOnlyList<OperationTimelineEvent> events, string transactionId)
    {
        bool HasEnd(string operation, string result) => events.Any(e =>
            e.EventKind == "end" && e.Operation == operation &&
            e.Result == result);
        bool HasStep(string step, string value) => events.Any(e =>
            e.EventKind == "step" && e.Step == step && e.Success == value);
        bool operatorCanceled = events.Any(e => e.Step == "card_issuing_cancelled" &&
            e.Reason == "user");
        bool watchdogCanceled = events.Any(e => e.Step == "card_issuing_cancelled" &&
            e.Reason == "watchdog");
        bool programCanceled = events.Any(e => e.Step == "card_issuing_cancelled" &&
            e.Reason == "programmatic");
        bool vtsClosed = HasEnd("CloseTransaction", "true") ||
            HasEnd("TryCloseEmptyTransaction", "true") ||
            HasEnd("VtsTransactionClose", "true");
        bool vtsRejected = HasEnd("VtsTransactionClose", "false") ||
            HasEnd("CloseTransaction", "false");
        bool receiptFailed = events.Any(e => e.EventKind == "end" &&
                (e.Operation == "PrintReceipt" ||
                 e.Operation == "UndoContractCurrentReceiptAttempt" ||
                 e.Operation == "UndoContractPreviousReceiptAttempt") &&
                e.Result == "false") ||
            HasStep("undo_receipt_print_result", "false") ||
            events.Any(e => e.Operation == "EndTransactionCommit" &&
                e.ResultName == "ReceiptsPrintedPartially");
        bool genericCardStatus = events.Any(e => e.Step == "card_commit_status" &&
            e.Category == "GenericError");
        bool commitAcknowledged = HasEnd("CardEmissionCommit", "true") ||
            HasEnd("CardDuplicationCommit", "true");

        string preview = "";
        OperationTimelineEvent? ready = events.FirstOrDefault(e =>
            e.Step == "card_preview_ready");
        if (ready is not null)
        {
            OperationTimelineEvent? decision = events.FirstOrDefault(e =>
                e.Timestamp >= ready.Timestamp &&
                (e.Step == "card_preview_decision" ||
                 e.Step == "card_issuing_cancelled"));
            if (decision is not null)
            {
                double seconds = (decision.Timestamp - ready.Timestamp).TotalSeconds;
                preview = " Preview-to-decision: " +
                    seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s " +
                    "(operator/preview interval, not service latency).";
            }
        }

        if (operatorCanceled)
        {
            return ("Cancelled by operator",
                "An explicit user cancellation was logged." +
                (genericCardStatus
                    ? " GenericError was committed as the cancellation status; this alone is not a device failure."
                    : "") +
                (commitAcknowledged ? " The commit API acknowledged the status." : "") +
                (vtsClosed ? " VTS was closed." : "") + preview);
        }
        if (watchdogCanceled)
            return ("Cancelled by watchdog",
                "A watchdog-triggered cancellation was logged. Do not call it a user decision." +
                (genericCardStatus ? " VTS received GenericError status." : "") + preview);
        if (programCanceled)
            return ("Cancelled by application",
                "A programmatic cancellation was recorded, not an operator click." + preview);
        if (vtsRejected)
            return ("VTS closure failed",
                "A VTS close returned false. Check the original response and subsequent retries.");
        if (receiptFailed)
            return (vtsClosed ? "VTS closed; printing incomplete" : "Receipt printing incomplete",
                "The receipt attempt or final receipt status indicates incomplete printing." +
                (vtsClosed ? " VTS closure independently succeeded." : "") + preview);
        if (genericCardStatus)
            return ("Card status GenericError; cause unknown",
                "GenericError is a reported card status, not proof of a hardware fault. " +
                "No explicit cancellation reason appears in the selected structured events.");
        if (HasEnd("EndTransactionCommit", "true") ||
            HasEnd("UndoContract", "true") || vtsClosed)
            return ("Completed (observed stages)",
                "A completion or successful VTS close was recorded." + preview);
        if (transactionId.Length == 0)
            return ("Flow observed; VTS link unknown",
                "No explicit, unambiguous VTS transaction link was found. " +
                "Do not attach this flow by timestamp proximity alone." + preview);
        return ("Outcome not established",
            "Structured stages exist, but no supported final result was observed." + preview);
    }

    private static bool Known(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "-";

    private static OperationTimelineEvent? Read(LogEvent entry)
    {
        if (!entry.Message.StartsWith("TOM_OP v=1 ", StringComparison.Ordinal))
            return null;

        string[] tokens = entry.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4 || tokens.Length > 64)
            return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 2; i < tokens.Length; i++)
        {
            int separator = tokens[i].IndexOf('=');
            if (separator <= 0)
                continue;
            string key = tokens[i][..separator];
            if (key.Length > 48 || fields.ContainsKey(key))
                continue;
            string raw = tokens[i][(separator + 1)..];
            try
            {
                fields[key] = Uri.UnescapeDataString(raw);
            }
            catch (UriFormatException)
            {
                fields[key] = raw;
            }
        }

        string Get(string name) => fields.TryGetValue(name, out string? value)
            ? value : "";
        static int? Number(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int number) ? number : null;
        string kind = Get("event");
        if (kind is not ("start" or "end" or "step" or "dialog" or "ui"))
            return null;

        return new OperationTimelineEvent(
            entry.Timestamp, kind, Get("operation"), Get("step"), Get("action"),
            Get("result"), Get("result_name"), Get("status"), Get("category"),
            Get("reason"), Get("success"), Get("response"), Get("dialog"),
            Get("view"), Get("handler"), Get("control"),
            Get("flow_id"), Get("transaction_id"), Get("session_id"),
            Get("process_id"), Get("operation_id"), Get("parent_operation_id"),
            Number(Get("duration_ms")), Number(Get("wait_ms")),
            entry.SourceFile, entry.LineNumber);
    }
}

public sealed record OperationTimelineEvent(
    DateTimeOffset Timestamp, string EventKind, string Operation, string Step,
    string Action, string Result, string ResultName, string Status,
    string Category, string Reason, string Success, string Response,
    string Dialog, string View, string Handler, string Control,
    string FlowId, string TransactionId, string SessionId, string ProcessId,
    string OperationId, string ParentOperationId, int? DurationMs, int? WaitMs,
    string SourceFile, int SourceLine)
{
    public string Description
    {
        get
        {
            string title = EventKind switch
            {
                "start" => Operation + " started",
                "end" => Operation + " finished",
                "step" => Step,
                "dialog" => "Dialog " + (Dialog.Length > 0 ? Dialog : "unknown") +
                    " " + Action,
                "ui" => "UI " + Action + " " + View + "." + Handler,
                _ => EventKind
            };
            var details = new List<string>();
            if (Result.Length > 0) details.Add("result=" + Result);
            if (ResultName.Length > 0) details.Add("result_name=" + ResultName);
            if (Success.Length > 0) details.Add("success=" + Success);
            if (Status.Length > 0) details.Add("status=" + Status);
            if (Category.Length > 0) details.Add("category=" + Category);
            if (Reason.Length > 0) details.Add("reason=" + Reason);
            if (Response.Length > 0) details.Add("response=" + Response);
            if (DurationMs.HasValue) details.Add(DurationMs.Value + " ms");
            if (WaitMs.HasValue) details.Add("wait=" + WaitMs.Value + " ms");
            return title + (details.Count == 0 ? "" : "  |  " + string.Join(", ", details));
        }
    }
}

public sealed record OperationTimelineGroup(
    string SessionId, string TransactionId, IReadOnlyList<string> FlowIds,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Outcome,
    string Explanation, int EventCount, IReadOnlyList<OperationTimelineEvent> Events);

public sealed record OperationTimelineReport(
    int StructuredEvents, int UnlinkedEvents, int GroupCount,
    IReadOnlyList<OperationTimelineGroup> Groups);
