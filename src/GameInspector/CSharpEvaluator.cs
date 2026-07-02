using System;
using System.IO;
using System.Text;
using Mono.CSharp;

namespace GameInspector
{
    public class CSharpEvaluator
    {
        private readonly Evaluator _evaluator;
        private readonly StringBuilder _reportOutput;
        private readonly ReportPrinter _reportPrinter;

        public CSharpEvaluator()
        {
            _reportOutput = new StringBuilder();
            var settings = new CompilerSettings
            {
                GenerateDebugInfo = false,
                WarningLevel = 0
            };

            _reportPrinter = new StreamReportPrinter(new StringWriter(_reportOutput));
            var context = new CompilerContext(settings, _reportPrinter);
            _evaluator = new Evaluator(context);

            InitializeImports();
        }

        private void InitializeImports()
        {
            // Reference assemblies first
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Skip dynamic assemblies
                    if (asm.IsDynamic) continue;

                    _evaluator.ReferenceAssembly(asm);
                }
                catch
                {
                    // Some assemblies can't be referenced, skip them
                }
            }

            // Core namespaces
            _evaluator.Run("using System;");
            _evaluator.Run("using System.Linq;");
            _evaluator.Run("using System.Collections;");
            _evaluator.Run("using System.Collections.Generic;");
            _evaluator.Run("using System.Reflection;");

            // Unity namespaces
            _evaluator.Run("using UnityEngine;");
            _evaluator.Run("using UnityEngine.SceneManagement;");

            // Import helpers
            _evaluator.Run("using GameInspector;");
            _evaluator.Run("using static GameInspector.Helpers;");

            GameInspectorPlugin.Log.LogInfo("C# Evaluator initialized with imports and helpers");
        }

        public EvalResult Evaluate(string code)
        {
            _reportOutput.Clear();

            try
            {
                object result = null;
                bool resultSet = false;

                // Evaluate returns a string error message or null if success
                var error = _evaluator.Evaluate(code, out result, out resultSet);

                if (!string.IsNullOrEmpty(error))
                {
                    // Compilation or runtime error
                    return new EvalResult
                    {
                        Success = false,
                        Error = error,
                        ErrorType = "EvaluationError"
                    };
                }

                // Check for compilation errors in report output
                if (_reportOutput.Length > 0)
                {
                    return new EvalResult
                    {
                        Success = false,
                        Error = _reportOutput.ToString().Trim(),
                        ErrorType = "CompilationError"
                    };
                }

                // If no result was set, it was likely a statement
                if (!resultSet)
                {
                    result = "Statement executed (no return value)";
                }

                return new EvalResult
                {
                    Success = true,
                    Result = result,
                    ResultType = result?.GetType().Name ?? "null"
                };
            }
            catch (Exception ex)
            {
                return new EvalResult
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorType = ex.GetType().Name,
                    StackTrace = ex.StackTrace
                };
            }
        }

        public class EvalResult
        {
            public bool Success { get; set; }
            public object Result { get; set; }
            public string ResultType { get; set; }
            public string Error { get; set; }
            public string ErrorType { get; set; }
            public string StackTrace { get; set; }
        }
    }
}
