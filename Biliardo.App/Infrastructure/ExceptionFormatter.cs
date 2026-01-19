using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Reflection;

namespace Biliardo.App.Infrastructure
{
    public static class ExceptionFormatter
    {
        public static string FormatUserMessage(Exception ex)
        {
            var baseEx = Unwrap(ex);
            var msg = baseEx.Message;
            if (string.IsNullOrWhiteSpace(msg))
                msg = "Errore imprevisto.";

            return $"{baseEx.GetType().Name}: {msg}";
        }

        public static string FormatDebugDetails(Exception ex)
        {
#if DEBUG
            var baseEx = Unwrap(ex);
            var sb = new StringBuilder();
            sb.AppendLine("Dettagli (debug):");
            sb.AppendLine(baseEx.ToString());
            return sb.ToString();
#else
            return string.Empty;
#endif
        }

        public static Exception Unwrap(Exception ex)
        {
            if (ex == null) return new Exception("Errore imprevisto.");

            Exception current = ex;

            while (true)
            {
                if (current is AggregateException agg && agg.InnerExceptions.Count > 0)
                {
                    current = agg.InnerExceptions.FirstOrDefault() ?? current;
                    continue;
                }

                if (current is TargetInvocationException tie && tie.InnerException != null)
                {
                    current = tie.InnerException;
                    continue;
                }

                if (current.InnerException != null && current != current.InnerException)
                {
                    current = current.InnerException;
                    continue;
                }

                break;
            }

            return current;
        }

        public static void Log(Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }
}
