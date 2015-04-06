﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kudu.TestHarness.Xunit
{
    public static class KuduXunitTestRunnerUtils
    {
        public static async Task<RunSummary> RunTestAsync(XunitTestRunner runner,
                                                          IMessageBus messageBus,
                                                          ExceptionAggregator aggregator,
                                                          bool disableRetry)
        {
            try
            {
                DelayedMessageBus delayedMessageBus = null;
                RunSummary summary = null;

                // First run
                if (!disableRetry)
                {
                    // This is really the only tricky bit: we need to capture and delay messages (since those will
                    // contain run status) until we know we've decided to accept the final result;
                    delayedMessageBus = new DelayedMessageBus(messageBus);

                    runner.SetMessageBus(delayedMessageBus);
                    summary = await RunTestInternalAsync(runner);

                    // if succeeded
                    if (summary.Failed == 0 || aggregator.HasExceptions)
                    {
                        delayedMessageBus.Flush(false);
                        return summary;
                    }
                }

                // Final run
                runner.SetMessageBus(new KuduTraceMessageBus(messageBus));
                summary = await RunTestInternalAsync(runner);

                // flush delay messages
                if (delayedMessageBus != null)
                {
                    delayedMessageBus.Flush(summary.Failed == 0 && !aggregator.HasExceptions);
                }

                return summary;
            }
            catch (Exception ex)
            {
                // this is catastrophic
                WriteLog(ex.ToString());
                throw;
            }
            finally
            {
                // set to original
                runner.SetMessageBus(messageBus);
            }
        }

        private static void WriteLog(string message)
        {
            try
            {
                var path = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory), "temp", "KuduXunitTestLog");
                Directory.CreateDirectory(path);
                path = Path.Combine(path, DateTime.UtcNow.ToString("yyyy-MM-ddTHH_mm_ssZ") + ".txt");
                File.WriteAllText(path, message);
            }
            catch
            {
                // no-op
            }
        }

        private static async Task<RunSummary> RunTestInternalAsync(XunitTestRunner runner)
        {
            TestTracer.InitializeContext();
            try
            {
                return await runner.RunAsync();
            }
            finally
            {
                TestTracer.FreeContext();
            }
        }

        public class DelayedMessageBus : IMessageBus
        {
            private readonly IMessageBus _innerBus;
            private readonly List<IMessageSinkMessage> _messages;

            public DelayedMessageBus(IMessageBus innerBus)
            {
                _innerBus = innerBus;
                _messages = new List<IMessageSinkMessage>();
            }

            public bool QueueMessage(IMessageSinkMessage message)
            {
                var result = message as TestResultMessage;
                if (result != null && String.IsNullOrEmpty(result.Output))
                {
                    // suwatch
                    result.SetOutput(TestTracer.GetTraceString(result.GetType().ToString(), result.Test == null ? "null" : result.Test.DisplayName));
                }

                lock (_messages)
                {
                    _messages.Add(message);
                }

                // No way to ask the inner bus if they want to cancel without sending them the message, so
                // we just go ahead and continue always.
                return true;
            }

            public void Dispose()
            {
            }

            public void Flush(bool retrySucceeded)
            {
                foreach (var message in _messages)
                {
                    // in case of retry succeeded, convert all failure to skip (ignored)
                    if (retrySucceeded && message is TestFailed)
                    {
                        var failed = (TestFailed)message;
                        var reason = new StringBuilder();
                        reason.AppendLine(String.Join(Environment.NewLine, failed.ExceptionTypes));
                        reason.AppendLine(String.Join(Environment.NewLine, failed.Messages));
                        reason.AppendLine(String.Join(Environment.NewLine, failed.StackTraces));

                        var skipped = new TestSkipped(failed.Test, reason.ToString());
                        skipped.SetOutput(failed.Output);
                        _innerBus.QueueMessage(skipped);
                    }
                    else
                    {
                        _innerBus.QueueMessage(message);
                    }
                }
            }
        }
    }
}
