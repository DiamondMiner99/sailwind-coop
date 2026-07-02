using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace GameInspector
{
    public class HttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Thread _listenerThread;
        private volatile bool _running;
        private readonly int _port;
        private CSharpEvaluator _evaluator;
        private readonly SafeSerializer _serializer = new SafeSerializer();
        private readonly object _evaluatorLock = new object();

        public HttpServer(int port = 7890)
        {
            _port = port;
            _listener = new HttpListener();
            // Bind to all interfaces for remote debugging
            // On Windows requires: netsh http add urlacl url=http://+:7890/ user=Everyone
            _listener.Prefixes.Add($"http://+:{port}/");
            _listenerThread = new Thread(ListenerLoop) { IsBackground = true };
            _evaluator = new CSharpEvaluator();
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            _listenerThread.Start();
            GameInspectorPlugin.Log.LogInfo($"HTTP server started on port {_port}");
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
            GameInspectorPlugin.Log.LogInfo("HTTP server stopped");
        }

        private void ListenerLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_running)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    GameInspectorPlugin.Log.LogError($"HTTP listener error: {ex}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url.AbsolutePath;
                var method = context.Request.HttpMethod;

                GameInspectorPlugin.Log.LogInfo($"HTTP {method} {path}");

                if (path == "/health" && method == "GET")
                {
                    SendJson(context.Response, new { status = "ok", port = _port });
                }
                else if (path == "/eval" && method == "POST")
                {
                    HandleEval(context);
                }
                else if (path == "/reset" && method == "POST")
                {
                    HandleReset(context);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    SendJson(context.Response, new { error = "Not found", availableEndpoints = new[] { "GET /health", "POST /eval", "POST /reset" } });
                }
            }
            catch (Exception ex)
            {
                GameInspectorPlugin.Log.LogError($"Request handler error: {ex}");
                try
                {
                    context.Response.StatusCode = 500;
                    SendJson(context.Response, new { error = ex.Message });
                }
                catch { }
            }
        }

        private void HandleEval(HttpListenerContext context)
        {
            string body;
            using (var reader = new StreamReader(context.Request.InputStream))
            {
                body = reader.ReadToEnd();
            }

            string code;
            try
            {
                var request = JsonConvert.DeserializeObject<EvalRequest>(body);
                // Prefer base64 if provided (avoids shell escaping issues)
                if (!string.IsNullOrEmpty(request?.CodeBase64))
                {
                    code = Encoding.UTF8.GetString(Convert.FromBase64String(request.CodeBase64));
                }
                else
                {
                    code = request?.Code;
                }
            }
            catch
            {
                // Fallback: treat body as raw code
                code = body;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                context.Response.StatusCode = 400;
                SendJson(context.Response, new { error = "No code provided" });
                return;
            }

            GameInspectorPlugin.Log.LogInfo($"Eval: {code.Substring(0, Math.Min(100, code.Length))}...");

            // Execute on main thread and wait for result
            CSharpEvaluator.EvalResult evalResult = null;
            var waitHandle = new ManualResetEvent(false);

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    lock (_evaluatorLock)
                    {
                        evalResult = _evaluator.Evaluate(code);

                        // Auto-recover from "builder already exists" corruption
                        if (!evalResult.Success && evalResult.Error != null &&
                            evalResult.Error.Contains("builder already exists"))
                        {
                            GameInspectorPlugin.Log.LogWarning("Evaluator corrupted, recreating...");
                            _evaluator = new CSharpEvaluator();
                            evalResult = _evaluator.Evaluate(code);
                        }
                    }
                }
                catch (Exception ex)
                {
                    evalResult = new CSharpEvaluator.EvalResult
                    {
                        Success = false,
                        Error = ex.Message,
                        ErrorType = "DispatcherError"
                    };
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            // Wait up to 30 seconds
            if (!waitHandle.WaitOne(30000))
            {
                context.Response.StatusCode = 504;
                SendJson(context.Response, new { error = "Evaluation timed out (30s)" });
                return;
            }

            if (evalResult.Success)
            {
                var serialized = _serializer.Serialize(evalResult.Result);
                SendJson(context.Response, new
                {
                    success = true,
                    result = serialized,
                    resultType = evalResult.ResultType
                });
            }
            else
            {
                context.Response.StatusCode = 400;
                SendJson(context.Response, new
                {
                    success = false,
                    error = evalResult.Error,
                    errorType = evalResult.ErrorType,
                    stackTrace = evalResult.StackTrace
                });
            }
        }

        private void HandleReset(HttpListenerContext context)
        {
            var waitHandle = new ManualResetEvent(false);
            Exception error = null;

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    lock (_evaluatorLock)
                    {
                        GameInspectorPlugin.Log.LogInfo("Resetting C# evaluator...");
                        _evaluator = new CSharpEvaluator();
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    waitHandle.Set();
                }
            });

            if (!waitHandle.WaitOne(10000))
            {
                context.Response.StatusCode = 504;
                SendJson(context.Response, new { error = "Reset timed out" });
                return;
            }

            if (error != null)
            {
                context.Response.StatusCode = 500;
                SendJson(context.Response, new { error = error.Message });
                return;
            }

            SendJson(context.Response, new { success = true, message = "Evaluator reset" });
        }

        private class EvalRequest
        {
            [JsonProperty("code")]
            public string Code { get; set; }

            [JsonProperty("code_base64")]
            public string CodeBase64 { get; set; }
        }

        private void SendJson(HttpListenerResponse response, object data)
        {
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        public void Dispose()
        {
            Stop();
            _listener.Close();
        }
    }
}
