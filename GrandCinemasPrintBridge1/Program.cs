// =====================================================================
// GrandCinemasPrintBridge — Program.cs
//
// Adds ONE temporary route for physical calibration:
//   POST http://localhost:9131/calibrate  (no body needed)
// Prints the mm-ruler calibration receipt from GdiReceiptPrinter and
// logs every printer-reported dimension to bridge.log. Remove this
// route (or just stop calling it) once calibration is finalized — it
// must never be reachable from the customer-facing kiosk flow.
//
// ── OPERATIONAL RELIABILITY (new) ────────────────────────────────────
// Three problems this section solves for a non-technical kiosk operator:
//
//  1. AUTO-START: previously nothing told Windows this should run — if
//     the kiosk PC restarted for any reason (power blip, Windows update,
//     someone rebooting it), the bridge stayed dead until a human
//     manually launched it again, with no warning that receipts would
//     fail. Now, on every successful start, the bridge writes itself
//     into HKCU\...\Run (the standard "launch at login" registry key),
//     so Windows starts it automatically at every login from then on —
//     no shortcut, no memory required.
//
//  2. SINGLE INSTANCE: a named Mutex is checked before anything else
//     starts. If the bridge is already running and someone double-clicks
//     its shortcut anyway (e.g. to check it's alive), they get a small
//     popup saying so, and nothing else happens — no second HttpListener
//     fighting over the same port.
//
//  3. VISIBLE STATUS FOR NON-TECH STAFF: instead of a console window
//     (easy to close by accident, meaningless to a non-technical
//     person), the bridge shows a small icon in the Windows system tray
//     whenever it's running, with a tooltip and an Exit option. No icon
//     in the tray = bridge is not running. That is the entire "how do
//     we tell" answer — no technical knowledge required.
// =====================================================================

using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GrandCinemasPrintBridge
{
    internal static class Program
    {
        private const string ListenPrefix = "http://localhost:9131/";

        // Any name unique enough not to collide with another app's Mutex.
        private const string SingleInstanceMutexName = "GrandCinemasPrintBridge_SingleInstance_9131";
        private const string AutoStartRegistryValueName = "GrandCinemasPrintBridge";

        private static NotifyIcon _trayIcon;

        [STAThread]
        private static void Main()
        {
            // ── Single-instance check ────────────────────────────────
            // `true` in the constructor means "I want to own this Mutex
            // immediately if nobody else does" — createdNew tells us
            // whether that succeeded. If it's false, another copy of the
            // bridge already holds it, i.e. is already running.
            bool createdNew;
            var singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show(
                    "Grand Cinemas Print Bridge is already running.\n\nLook for its icon in the system tray (bottom-right, near the clock).",
                    "Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            EnsureAutoStartRegistered();

            // HttpListener runs on its own background thread — the main
            // (STA) thread is needed free to run the tray icon's message
            // loop (Application.Run), which NotifyIcon requires to work.
            var listenerThread = new Thread(RunHttpListener);
            listenerThread.IsBackground = true;
            listenerThread.Start();

            RunTrayIcon();

            // Keeps the Mutex alive (and thus "held") for the entire
            // process lifetime — releasing it only happens implicitly on
            // process exit, which is exactly what we want.
            GC.KeepAlive(singleInstanceMutex);
        }

        // Registers this exe to launch automatically the next time this
        // Windows user logs in. HKEY_CURRENT_USER (not LOCAL_MACHINE) is
        // used deliberately — it needs no admin rights and only affects
        // the kiosk's own login session, which is exactly where the
        // printer driver and this exe both need to run.
        private static void EnsureAutoStartRegistered()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
                {
                    if (key == null) return;
                    var existing = key.GetValue(AutoStartRegistryValueName) as string;
                    string desired = "\"" + exePath + "\"";
                    if (!string.Equals(existing, desired, StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(AutoStartRegistryValueName, desired);
                        Log("Auto-start registered: " + desired);
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let auto-start registration block the bridge itself
                // from running today, even if it couldn't set up tomorrow.
                Log("Could not register auto-start: " + ex.Message);
            }
        }

        // System tray icon — the plain-language "is it running" signal
        // for a non-technical operator. Uses the exe's own icon so it's
        // immediately recognizable, with a fallback to a stock icon if
        // that's ever unavailable.
        private static void RunTrayIcon()
        {
            Icon icon;
            try
            {
                icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location)
                       ?? SystemIcons.Application;
            }
            catch
            {
                icon = SystemIcons.Application;
            }

            var menu = new ContextMenuStrip();
            var statusItem = new ToolStripMenuItem("Grand Cinemas Print Bridge — Running") { Enabled = false };
            var openLogsItem = new ToolStripMenuItem("Open Logs Folder");
            openLogsItem.Click += (s, e) =>
            {
                try
                {
                    string logsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                    Directory.CreateDirectory(logsFolder);
                    System.Diagnostics.Process.Start("explorer.exe", logsFolder);
                }
                catch (Exception ex)
                {
                    Log("Could not open logs folder: " + ex.Message);
                }
            };
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => { Application.Exit(); };

            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(openLogsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "Grand Cinemas Print Bridge — Running",
                ContextMenuStrip = menu,
                Visible = true,
            };

            Application.ApplicationExit += (s, e) =>
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            };

            Application.Run();
        }

        private static void RunHttpListener()
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(ListenPrefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                Log(
                    "Could not start listening on " + ListenPrefix +
                    " — " + ex.Message +
                    " — try running as Administrator once, or: netsh http add urlacl url=" +
                    ListenPrefix + " user=Everyone");
                return;
            }

            Log("Listening on " + ListenPrefix);

            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(async _ => await HandleRequest(ctx));
            }
        }

        private static async Task HandleRequest(HttpListenerContext ctx)
        {
            var response = ctx.Response;

            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = ctx.Request.Url.AbsolutePath;
            bool isPrintRoute = ctx.Request.HttpMethod == "POST" && path == "/print";
            bool isPreviewRoute = ctx.Request.HttpMethod == "POST" && path == "/preview";
            bool isCalibrateRoute = ctx.Request.HttpMethod == "POST" && path == "/calibrate";

            if (isCalibrateRoute)
            {
                try
                {
                    var (success, reason) = GdiReceiptPrinter.PrintCalibration();
                    if (!success)
                    {
                        Log("Calibration print failed — reason: " + (reason ?? "unspecified"));
                        WriteJson(response, "{\"success\":false,\"error\":\"" + JsonEscape(reason ?? "unspecified") + "\"}");
                    }
                    else
                    {
                        WriteJson(response, "{\"success\":true,\"message\":\"Calibration receipt printed — check bridge.log for the measured values.\"}");
                    }
                }
                catch (Exception ex)
                {
                    Log("EXCEPTION in /calibrate: " + ex);
                    WriteJson(response, "{\"success\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}");
                }
                return;
            }

            if (!isPrintRoute && !isPreviewRoute)
            {
                response.StatusCode = 404;
                WriteJson(response, "{\"success\":false,\"error\":\"not_found\"}");
                return;
            }

            try
            {
                string body;
                // FIX (Arabic mojibake root cause): ctx.Request.ContentEncoding is
                // NEVER null here, so the "?? Encoding.UTF8" fallback below never
                // actually ran. When the frontend's fetch() call sends
                // "Content-Type: application/json" with no explicit charset
                // parameter (confirmed in localBridgePrinter.js), HttpListenerRequest
                // .ContentEncoding does not throw or return null — it falls back to
                // Encoding.Default, i.e. the OS's ANSI codepage (e.g. Windows-1252 on
                // a US/UK-localized Windows box), not UTF-8. The frontend always
                // sends the body as UTF-8 bytes (JSON.stringify() + fetch() are UTF-8
                // by spec, regardless of any declared/missing charset), so decoding
                // with Encoding.Default silently mangles any non-ASCII byte sequence
                // (Arabic) while leaving ASCII (English, digits, punctuation)
                // untouched — exactly the reported symptom. Decode unconditionally as
                // UTF-8 instead of trusting ContentEncoding's guess.
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                ReceiptPrintRequest request;
                try
                {
                    request = JsonConvert.DeserializeObject<ReceiptPrintRequest>(body);
                }
                catch (JsonException ex)
                {
                    Log("invalid_request — JSON parse failure: " + ex.Message);
                    WriteJson(response, "{\"success\":false,\"error\":\"invalid_request\"}");
                    return;
                }

                if (request?.Elements == null || request.Elements.Count == 0)
                {
                    Log("empty_elements — request parsed but contained no elements.");
                    WriteJson(response, "{\"success\":false,\"error\":\"empty_elements\"}");
                    return;
                }

                if (isPreviewRoute)
                {
                    var (pSuccess, pReason, pFilePath) = GdiReceiptPrinter.Preview(request);
                    if (!pSuccess)
                    {
                        Log("Preview failed — reason: " + (pReason ?? "unspecified"));
                        WriteJson(response, "{\"success\":false,\"error\":\"" + JsonEscape(pReason ?? "unspecified") + "\"}");
                    }
                    else
                    {
                        WriteJson(response, "{\"success\":true,\"filePath\":\"" + JsonEscape(pFilePath) + "\"}");
                    }
                    return;
                }

                var (success, reason) = GdiReceiptPrinter.Print(request);

                // FIX: PrintLogger.LogPrintAttempt() is what creates
                // Logs/<dd-MM-yyyy>/PrintLog_*.txt (via Directory.CreateDirectory
                // inside it) — fully built, but nothing was ever calling it, so
                // that folder could never appear no matter how many real prints
                // went through. Only real /print requests are logged here (per
                // PrintLogger's own contract) — /preview is intentionally excluded
                // above so test traffic never pollutes the daily counts.
                PrintLogger.LogPrintAttempt(
                    body,
                    request.ReceiptType,
                    request.BookingId,
                    request.WorkstationId,
                    request.CinemaId,
                    success,
                    reason);

                if (!success)
                {
                    Log("Print failed — reason: " + (reason ?? "unspecified"));
                    WriteJson(response, "{\"success\":false,\"error\":\"" + JsonEscape(reason ?? "unspecified") + "\"}");
                }
                else
                {
                    WriteJson(response, "{\"success\":true}");
                }
            }
            catch (Exception ex)
            {
                Log("EXCEPTION: " + ex);
                WriteJson(response, "{\"success\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}");
            }
        }

        private static readonly object _logLock = new object();
        internal static void Log(string message)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge.log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine;
                lock (_logLock)
                {
                    File.AppendAllText(logPath, line);
                }
            }
            catch
            {
                // Logging must never crash the print path.
            }
        }

        private static void WriteJson(HttpListenerResponse response, string json)
        {
            response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private static string JsonEscape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }
    }
}