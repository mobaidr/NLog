// 
// Copyright (c) 2004-2017 Jaroslaw Kowalski <jaak@jkowalski.net>, Kim Christensen, Julian Verdurmen
// 
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without 
// modification, are permitted provided that the following conditions 
// are met:
// 
// * Redistributions of source code must retain the above copyright notice, 
//   this list of conditions and the following disclaimer. 
// 
// * Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution. 
// 
// * Neither the name of Jaroslaw Kowalski nor the names of its 
//   contributors may be used to endorse or promote products derived from this
//   software without specific prior written permission. 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
// ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF 
// THE POSSIBILITY OF SUCH DAMAGE.
// 

namespace NLog.Targets
{
    using System;
    using System.Reflection;
    using System.Collections.Generic;
    using System.Linq;
    using Common;
    using Internal;

    /// <summary>
    /// Calls the specified static method on each log message and passes contextual parameters to it.
    /// </summary>
    /// <seealso href="https://github.com/nlog/nlog/wiki/MethodCall-target">Documentation on NLog Wiki</seealso>
    /// <example>
    /// <p>
    /// To set up the target in the <a href="config.html">configuration file</a>, 
    /// use the following syntax:
    /// </p>
    /// <code lang="XML" source="examples/targets/Configuration File/MethodCall/NLog.config" />
    /// <p>
    /// This assumes just one target and a single rule. More configuration
    /// options are described <a href="config.html">here</a>.
    /// </p>
    /// <p>
    /// To set up the log target programmatically use code like this:
    /// </p>
    /// <code lang="C#" source="examples/targets/Configuration API/MethodCall/Simple/Example.cs" />
    /// </example>
    [Target("MethodCall")]
    public sealed class MethodCallTarget : MethodCallTargetBase
    {
        /// <summary>
        /// Gets or sets the class name.
        /// </summary>
        /// <docgen category='Invocation Options' order='10' />
        public string ClassName { get; set; }

        /// <summary>
        /// Gets or sets the method name. The method must be public and static.
        /// 
        /// Use the AssemblyQualifiedName , https://msdn.microsoft.com/en-us/library/system.type.assemblyqualifiedname(v=vs.110).aspx
        /// e.g. 
        /// </summary>
        /// <docgen category='Invocation Options' order='10' />
        public string MethodName { get; set; }

        private MethodInfo Method { get; set; }

        private int NeededParameters { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MethodCallTarget" /> class.
        /// </summary>
        public MethodCallTarget() : base()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MethodCallTarget" /> class.
        /// </summary>
        /// <param name="name">Name of the target.</param>
        public MethodCallTarget(string name) : this()
        {
            Name = name;
        }

        /// <summary>
        /// Initializes the target.
        /// </summary>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            if (ClassName != null && MethodName != null)
            {
                Type targetType = Type.GetType(ClassName);

                if (targetType != null)
                {
                    Method = targetType.GetMethod(MethodName);
                    if (Method == null)
                    {
                        InternalLogger.Warn("Initialize MethodCallTarget, method '{0}' in class '{1}' not found - it should be static", MethodName, ClassName);
                    }
                    else
                    {
                        NeededParameters = Method.GetParameters().Length;
                    }
                }
                else
                {
                    InternalLogger.Warn("Initialize MethodCallTarget, class '{0}' not found", ClassName);
                    Method = null;
                }
            }
            else
            {
                Method = null;
            }
        }

        /// <summary>
        /// Calls the specified Method.
        /// </summary>
        /// <param name="parameters">Method parameters.</param>
        protected override void DoInvoke(object[] parameters)
        {
            if (Method != null)
            {
                var missingParameters = NeededParameters - parameters.Length;
                if (missingParameters > 0)
                {
                    //fill missing parameters with Type.Missing
                    var newParams = new List<object>(parameters);
                    newParams.AddRange(Enumerable.Repeat(Type.Missing, missingParameters));
                    parameters = newParams.ToArray();
                }

                Method.Invoke(null, parameters);
            }
            else
            {
                InternalLogger.Trace("No invoke because class/method was not found or set");
            }
        }
    }
}
