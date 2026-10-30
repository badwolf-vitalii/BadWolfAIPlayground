using System.Text;

namespace BadWolfAIPlayground.Anomaly;

/// <summary>Deterministic sample, with a real spike and a novel error template.</summary>
public static class DemoLogs
{
    public static string Create()
    {
        var start = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        var builder = new StringBuilder(48_000);
        for (int minute = 0; minute < 36; minute++)
        {
            for (int i = 0; i < 6; i++)
            {
                Append(start.AddMinutes(minute).AddSeconds(i), "INFO",
                    $"Request completed in {22 + i}ms for client {minute * 10 + i}");
            }
            Append(start.AddMinutes(minute).AddSeconds(10), "ERROR",
                $"Retry timeout on connection {minute * 10 + 9}");

            if (minute == 24)
            {
                // Simulate a sudden error storm using a familiar template.
                for (int i = 0; i < 12; i++)
                {
                    Append(start.AddMinutes(minute).AddSeconds(20 + i), "ERROR",
                        $"Retry timeout on connection {9000 + i}");
                }
            }
            if (minute == 31)
            {
                Append(start.AddMinutes(minute).AddSeconds(42), "FATAL",
                    "Disk controller rejected write to sector 48392");
                // Synthetic exception detail is deliberately undated: it
                // belongs to the FATAL entry and is not a second event.
                builder.AppendLine("System.IO.IOException: Synthetic disk write failure");
                builder.AppendLine(@"   at Demo.Storage.WriteSector(Int32 sector) in C:\Demo\Storage.cs:line 82");
                builder.AppendLine("   at Demo.Worker.Process()");
            }
        }
        return builder.ToString();

        void Append(DateTimeOffset moment, string severity, string message) =>
            builder.Append(moment.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append(" [").Append(severity).Append("] ")
                .AppendLine(message);
    }
}
