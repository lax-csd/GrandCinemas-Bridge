using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
// =====================================================================
// PrintLogger.cs — GrandCinemasPrintBridge
//
// Structured, date-organized logging for actual print jobs — separate
// from Program.cs's bridge.log (which is internal diagnostic noise:
// "Listening on...", "PrintPage fired.", etc). This file is specifically
// the business-facing record: every ticket/receipt printed, organized
// by day, in a format modeled on the client's original system's log
// style (Time Stamp / WorkstationID / CinemaID / OrderID / Print Type /
// Request JSON), plus running daily counts and a dedicated error log
// for fast troubleshooting.
//
// Folder layout created under the exe's own folder:
//   Logs/
//     10-09-2026/
//       PrintLog_10-09-2026.txt   ← every attempt, success or fail
//       Errors_10-09-2026.txt     ← FAILED attempts only, same detail
//       Summary_10-09-2026.txt    ← running counts, rewritten after each print
//
// Only real /print requests are logged here — /preview (testing) does
// not affect these files or counts, so test traffic never pollutes the
// real production numbers.
// =====================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace GrandCinemasPrintBridge
{
    public static class PrintLogger
    {
        private static readonly object _lock = new object();
        private const string LogsRootFolderName = "Logs";

        // In-memory counts for whichever date was last touched — reloaded
        // from the Summary file whenever the date changes (including on
        // first use after an app restart), so counts survive restarts
        // and correctly reset at midnight.
        private static string _countsLoadedForDate;
        private static Dictionary<string, int> _counts = new Dictionary<string, int>();

        private static readonly string[] KnownCategories =
        {
            "CollectTicket", "SuccessTicket", "PurchaseReceipt", "FailureReceipt", "Unknown"
        };

        /// <summary>
        /// Call this once per REAL /print request, after GdiReceiptPrinter.Print()
        /// has returned its result. Writes the structured log entry, updates the
        /// dedicated error log if it failed, and updates the running daily counts.
        /// </summary>
        public static void LogPrintAttempt(
            string rawRequestBody,
            string receiptType,
            string bookingId,
            string workstationId,
            string cinemaId,
            bool success,
            string failureReason)
        {
            lock (_lock)
            {
                try
                {
                    DateTime now = DateTime.Now;
                    string dateStr = now.ToString("dd-MM-yyyy");
                    string dateFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogsRootFolderName, dateStr);
                    Directory.CreateDirectory(dateFolder);

                    string category = CategoryFor(receiptType);

                    string entry = BuildLogEntry(now, workstationId, cinemaId, bookingId, receiptType, rawRequestBody, success, failureReason);

                    string mainLogPath = Path.Combine(dateFolder, "PrintLog_" + dateStr + ".txt");
                    File.AppendAllText(mainLogPath, entry);

                    if (!success)
                    {
                        string errorLogPath = Path.Combine(dateFolder, "Errors_" + dateStr + ".txt");
                        File.AppendAllText(errorLogPath, entry);
                    }

                    UpdateCounts(dateStr, dateFolder, category, success);
                }
                catch (Exception ex)
                {
                    // Logging must never crash the print path — fall back to the
                    // bridge's own internal diagnostic log if this fails.
                    Program.Log("PrintLogger.LogPrintAttempt failed: " + ex.Message);
                }
            }
        }

        private static string BuildLogEntry(
            DateTime now, string workstationId, string cinemaId, string bookingId,
            string receiptType, string rawRequestBody, bool success, string failureReason)
        {
            string ts = now.ToString("dd-MM-yyyy hh:mm:ss tt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Time Stamp : " + ts);
            sb.AppendLine("WorkstationID    : " + (string.IsNullOrEmpty(workstationId) ? "N/A" : workstationId));
            sb.AppendLine("CinemaID         : " + (string.IsNullOrEmpty(cinemaId) ? "N/A" : cinemaId));
            sb.AppendLine("OrderID          : " + (string.IsNullOrEmpty(bookingId) ? "N/A" : bookingId));
            sb.AppendLine("Method           : print");
            sb.AppendLine("Print Type       : " + (receiptType ?? "Unknown"));
            sb.AppendLine();
            sb.AppendLine("Request: " + ts);
            sb.AppendLine();
            sb.AppendLine(rawRequestBody ?? "(empty)");
            sb.AppendLine();
            sb.AppendLine("Result           : " + (success ? "SUCCESS" : "FAILED - " + (failureReason ?? "unspecified")));
            sb.AppendLine();
            sb.AppendLine("---------------------------------------------------------------------------");
            sb.AppendLine();
            sb.AppendLine();
            return sb.ToString();
        }

        // Ordered so "CollectTicket" (which also contains "ticket") is
        // never mis-bucketed as SuccessTicket. Works with plain values
        // ("SuccessTicket") and suffixed test values ("SuccessTicket_Multiple_1").
        private static string CategoryFor(string receiptType)
        {
            if (string.IsNullOrEmpty(receiptType)) return "Unknown";
            string t = receiptType.ToLowerInvariant();
            if (t.Contains("collect")) return "CollectTicket";
            if (t.Contains("purchase")) return "PurchaseReceipt";
            if (t.Contains("failure")) return "FailureReceipt";
            if (t.Contains("success") || t.Contains("ticket")) return "SuccessTicket";
            return "Unknown";
        }

        private static void UpdateCounts(string dateStr, string dateFolder, string category, bool success)
        {
            if (_countsLoadedForDate != dateStr)
            {
                LoadCountsFromSummary(dateStr, dateFolder);
            }

            string key = success ? category : category + "_Failed";
            if (!_counts.ContainsKey(key)) _counts[key] = 0;
            _counts[key]++;

            if (!success)
            {
                if (!_counts.ContainsKey("Errors")) _counts["Errors"] = 0;
                _counts["Errors"]++;
            }

            WriteSummary(dateStr, dateFolder);
        }

        private static void LoadCountsFromSummary(string dateStr, string dateFolder)
        {
            _counts = new Dictionary<string, int>();
            _countsLoadedForDate = dateStr;

            string summaryPath = Path.Combine(dateFolder, "Summary_" + dateStr + ".txt");
            if (!File.Exists(summaryPath)) return;

            try
            {
                foreach (string line in File.ReadAllLines(summaryPath))
                {
                    int colonIdx = line.IndexOf(':');
                    if (colonIdx <= 0) continue;
                    string key = line.Substring(0, colonIdx).Trim();
                    string valueStr = line.Substring(colonIdx + 1).Trim();
                    if (int.TryParse(valueStr, out int value))
                    {
                        _counts[key] = value;
                    }
                }
            }
            catch
            {
                // If the summary file is malformed, start fresh rather than crash.
                _counts = new Dictionary<string, int>();
            }
        }

        private static void WriteSummary(string dateStr, string dateFolder)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Date : " + dateStr);
            sb.AppendLine();
            sb.AppendLine("CollectTicket    : " + GetCount("CollectTicket"));
            sb.AppendLine("SuccessTicket    : " + GetCount("SuccessTicket"));
            sb.AppendLine("PurchaseReceipt  : " + GetCount("PurchaseReceipt"));
            sb.AppendLine("FailureReceipt   : " + GetCount("FailureReceipt"));
            if (GetCount("Unknown") > 0)
            {
                sb.AppendLine("Unknown          : " + GetCount("Unknown"));
            }
            sb.AppendLine();
            sb.AppendLine("Errors           : " + GetCount("Errors"));
            sb.AppendLine();
            sb.AppendLine("Last Updated     : " + DateTime.Now.ToString("dd-MM-yyyy hh:mm:ss tt"));

            string summaryPath = Path.Combine(dateFolder, "Summary_" + dateStr + ".txt");
            File.WriteAllText(summaryPath, sb.ToString());
        }

        private static int GetCount(string key)
        {
            return _counts.ContainsKey(key) ? _counts[key] : 0;
        }
    }
}
namespace GrandCinemasPrintBridge1
{
    internal class Printlogger
    {
    }
}
