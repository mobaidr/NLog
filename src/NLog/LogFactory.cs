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

namespace NLog
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Security;
    using System.Text;
    using System.Threading;

    using JetBrains.Annotations;

    using Common;
    using Config;
    using Internal;
    using Targets;
    using Internal.Fakeables;

#if SILVERLIGHT && !__IOS__ && !__ANDROID__
    using System.Windows;
#endif

    /// <summary>
    /// Creates and manages instances of <see cref="T:NLog.Logger" /> objects.
    /// </summary>
    public class LogFactory : IDisposable
    {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        private const int ReconfigAfterFileChangedTimeout = 1000;
        internal Timer reloadTimer;
        private readonly MultiFileWatcher _watcher;
#endif

        private readonly static TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(15);

        private static IAppDomain currentAppDomain;

        /// <remarks>
        /// Internal for unit tests
        /// </remarks>
        internal readonly object _syncRoot = new object();

        private LoggingConfiguration _config;
        private LogLevel _globalThreshold = LogLevel.MinLevel;
        private bool _configLoaded;
        // TODO: logsEnabled property might be possible to be encapsulated into LogFactory.LogsEnabler class. 
        private int _logsEnabled;
        private readonly LoggerCache _loggerCache = new LoggerCache();

        /// <summary>
        /// Overwrite possible file paths (including filename) for possible NLog config files. 
        /// When this property is <c>null</c>, the default file paths (<see cref="GetCandidateConfigFilePaths"/> are used.
        /// </summary>
        private List<string> _candidateConfigFilePaths;

        /// <summary>
        /// Occurs when logging <see cref="Configuration" /> changes.
        /// </summary>
        public event EventHandler<LoggingConfigurationChangedEventArgs> ConfigurationChanged;

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        /// <summary>
        /// Occurs when logging <see cref="Configuration" /> gets reloaded.
        /// </summary>
        public event EventHandler<LoggingConfigurationReloadedEventArgs> ConfigurationReloaded;
#endif

        private static event EventHandler<EventArgs> LoggerShutdown;

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        /// <summary>
        /// Initializes static members of the LogManager class.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "Significant logic in .cctor()")]
        static LogFactory()
        {
            RegisterEvents(CurrentAppDomain);
        }
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="LogFactory" /> class.
        /// </summary>
        public LogFactory()
        {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
            _watcher = new MultiFileWatcher();
            _watcher.FileChanged += ConfigFileChanged;
            LoggerShutdown += OnStopLogging;
#endif
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LogFactory" /> class.
        /// </summary>
        /// <param name="config">The config.</param>
        public LogFactory(LoggingConfiguration config)
            : this()
        {
            Configuration = config;
        }

        /// <summary>
        /// Gets the current <see cref="IAppDomain"/>.
        /// </summary>
        public static IAppDomain CurrentAppDomain
        {
            get
            {
                return currentAppDomain ??
#if NETSTANDARD1_5
                    (currentAppDomain = new FakeAppDomain());
#else
                    (currentAppDomain = new AppDomainWrapper(AppDomain.CurrentDomain));
#endif
            }
            set
            {
                UnregisterEvents(currentAppDomain);
                //make sure we aren't double registering.
                UnregisterEvents(value);
                RegisterEvents(value);
                currentAppDomain = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether exceptions should be thrown. See also <see cref="ThrowConfigExceptions"/>.
        /// </summary>
        /// <value>A value of <c>true</c> if exception should be thrown; otherwise, <c>false</c>.</value>
        /// <remarks>By default exceptions are not thrown under any circumstances.</remarks>
        public bool ThrowExceptions { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether <see cref="NLogConfigurationException"/> should be thrown.
        /// 
        /// If <c>null</c> then <see cref="ThrowExceptions"/> is used.
        /// </summary>
        /// <value>A value of <c>true</c> if exception should be thrown; otherwise, <c>false</c>.</value>
        /// <remarks>
        /// This option is for backwards-compatiblity.
        /// By default exceptions are not thrown under any circumstances.
        /// </remarks>
        public bool? ThrowConfigExceptions { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether Variables should be kept on configuration reload.
        /// Default value - false.
        /// </summary>
        public bool KeepVariablesOnReload { get; set; }


        /// <summary>
        /// Gets or sets the current logging configuration. After setting this property all
        /// existing loggers will be re-configured, so there is no need to call <see cref="ReconfigExistingLoggers" />
        /// manually.
        /// </summary>
        public LoggingConfiguration Configuration
        {
            get
            {
                if (_configLoaded)
                    return _config;

                lock (_syncRoot)
                {
                    if (_configLoaded)
                        return _config;

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__ && !NETSTANDARD
                    //load

                    if (_config == null)
                    {
                        try
                        {
                            // Try to load default configuration.
                            _config = XmlLoggingConfiguration.AppConfig;
                        }
                        catch (Exception ex)
                        {
                            //loading could fail due to an invalid XML file (app.config) etc.
                            if (ex.MustBeRethrown())
                            {
                                throw;
                            }

                        }
                    }
#endif
                    // Retest the condition as we might have loaded a config.
                    if (_config == null)
                    {
                        var configFileNames = GetCandidateConfigFilePaths();
                        foreach (string configFile in configFileNames)
                        {
#if SILVERLIGHT && !WINDOWS_PHONE
                            Uri configFileUri = new Uri(configFile, UriKind.Relative);
                            if (Application.GetResourceStream(configFileUri) != null)
                            {
                                LoadLoggingConfiguration(configFile);
                                break;
                            }
#else
                            if (File.Exists(configFile))
                            {
                                LoadLoggingConfiguration(configFile);
                                break;
                            }
#endif
                        }
                    }

#if __ANDROID__
                    if (this._config == null)
                    {
                        //try nlog.config in assets folder
                        const string nlogConfigFilename = "NLog.config";
                        try
                        {
                            using (var stream = Android.App.Application.Context.Assets.Open(nlogConfigFilename))
                            {
                                if (stream != null)
                                {
                                    LoadLoggingConfiguration(XmlLoggingConfiguration.AssetsPrefix + nlogConfigFilename);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            InternalLogger.Trace(e, "no {0} in assets folder", nlogConfigFilename);
                        }

                    }
#endif

                    if (_config != null)
                    {
                        try
                        {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
                            _config.Dump();
                            try
                            {
                                _watcher.Watch(_config.FileNamesToWatch);
                            }
                            catch (Exception exception)
                            {
                                if (exception.MustBeRethrownImmediately())
                                {
                                    throw;
                                }

                                InternalLogger.Warn(exception, "Cannot start file watching. File watching is disabled");
                                //TODO NLog 5: check "MustBeRethrown" 
                            }
#endif
                            _config.InitializeAll();

                            LogConfigurationInitialized();
                        }
                        finally
                        {
                            _configLoaded = true;
                        }
                    }

                    return _config;
                }
            }

            set
            {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
                try
                {
                    _watcher.StopWatching();
                }
                catch (Exception exception)
                {
                    InternalLogger.Error(exception, "Cannot stop file watching.");

                    if (exception.MustBeRethrown())
                    {
                        throw;
                    }
                }
#endif

                lock (_syncRoot)
                {
                    LoggingConfiguration oldConfig = _config;
                    if (oldConfig != null)
                    {
                        InternalLogger.Info("Closing old configuration.");
#if !SILVERLIGHT
                        Flush();
#endif
                        oldConfig.Close();
                    }

                    _config = value;

                    if (_config == null)
                        _configLoaded = false;
                    else
                    {
                        try
                        {
                            _config.Dump();

                            _config.InitializeAll();
                            ReconfigExistingLoggers();
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
                            try
                            {
                                _watcher.Watch(_config.FileNamesToWatch);
                            }
                            catch (Exception exception)
                            {
                                //ToArray needed for .Net 3.5
                                InternalLogger.Warn(exception, "Cannot start file watching: {0}", String.Join(",", _config.FileNamesToWatch.ToArray()));

                                if (exception.MustBeRethrown())
                                {
                                    throw;
                                }
                            }
#endif
                        }
                        finally
                        {
                            _configLoaded = true;
                        }
                    }

                    OnConfigurationChanged(new LoggingConfigurationChangedEventArgs(value, oldConfig));
                }
            }
        }

        /// <summary>
        /// Gets or sets the global log level threshold. Log events below this threshold are not logged.
        /// </summary>
        public LogLevel GlobalThreshold
        {
            get => _globalThreshold;

            set
            {
                lock (_syncRoot)
                {
                    _globalThreshold = value;
                    ReconfigExistingLoggers();
                }
            }
        }

        /// <summary>
        /// Gets the default culture info to use as <see cref="LogEventInfo.FormatProvider"/>.
        /// </summary>
        /// <value>
        /// Specific culture info or null to use <see cref="CultureInfo.CurrentCulture"/>
        /// </value>
        [CanBeNull]
        public CultureInfo DefaultCultureInfo
        {
            get
            {
                var configuration = Configuration;
                return configuration != null ? configuration.DefaultCultureInfo : null;
            }
        }

        internal static void LogConfigurationInitialized()
        {
            InternalLogger.Info("Configuration initialized.");
            try
            {
                InternalLogger.LogAssemblyVersion(typeof(ILogger).GetAssembly());
            }
            catch (SecurityException ex)
            {
                InternalLogger.Debug(ex, "Not running in full trust");
            }

        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting 
        /// unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Creates a logger that discards all log messages.
        /// </summary>
        /// <returns>Null logger instance.</returns>
        public Logger CreateNullLogger()
        {
            return new NullLogger(this);
        }

        /// <summary>
        /// Gets the logger with the name of the current class. 
        /// </summary>
        /// <returns>The logger.</returns>
        /// <remarks>This is a slow-running method. 
        /// Make sure you're not doing this in a loop.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Logger GetCurrentClassLogger()
        {
#if NETSTANDARD1_5
            return this.GetLogger(StackTraceUsageUtils.GetClassFullName());
#else
#if SILVERLIGHT
            var frame = new StackFrame(1);
#else
            var frame = new StackFrame(1, false);
#endif

            return GetLogger(frame.GetMethod().DeclaringType.FullName);
#endif
        }

        /// <summary>
        /// Gets the logger with the name of the current class. 
        /// </summary>
        /// <returns>The logger with type <typeparamref name="T"/>.</returns>
        /// <typeparam name="T">Type of the logger</typeparam>
        /// <remarks>This is a slow-running method. 
        /// Make sure you're not doing this in a loop.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public T GetCurrentClassLogger<T>() where T : Logger
        {
#if NETSTANDARD1_5
            return (T)this.GetLogger(StackTraceUsageUtils.GetClassFullName(), typeof(T));
#else
#if SILVERLIGHT
            var frame = new StackFrame(1);
#else
            var frame = new StackFrame(1, false);
#endif

            return (T)GetLogger(frame.GetMethod().DeclaringType.FullName, typeof(T));
#endif
        }

        /// <summary>
        /// Gets a custom logger with the name of the current class. Use <paramref name="loggerType"/> to pass the type of the needed Logger.
        /// </summary>
        /// <param name="loggerType">The type of the logger to create. The type must inherit from <see cref="Logger"/></param>
        /// <returns>The logger of type <paramref name="loggerType"/>.</returns>
        /// <remarks>This is a slow-running method. Make sure you are not calling this method in a 
        /// loop.</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Logger GetCurrentClassLogger(Type loggerType)
        {
#if NETSTANDARD1_5
            return this.GetLogger(StackTraceUsageUtils.GetClassFullName(), loggerType);
#else
#if SILVERLIGHT
            var frame = new StackFrame(1);
#else
            var frame = new StackFrame(1, false);
#endif

            return GetLogger(frame.GetMethod().DeclaringType.FullName, loggerType);
#endif
        }

        /// <summary>
        /// Gets the specified named logger.
        /// </summary>
        /// <param name="name">Name of the logger.</param>
        /// <returns>The logger reference. Multiple calls to <c>GetLogger</c> with the same argument 
        /// are not guaranteed to return the same logger reference.</returns>
        public Logger GetLogger(string name)
        {
            return GetLogger(new LoggerCacheKey(name, typeof(Logger)));
        }

        /// <summary>
        /// Gets the specified named logger.
        /// </summary>
        /// <param name="name">Name of the logger.</param>
        /// <typeparam name="T">Type of the logger</typeparam>
        /// <returns>The logger reference with type <typeparamref name="T"/>. Multiple calls to <c>GetLogger</c> with the same argument 
        /// are not guaranteed to return the same logger reference.</returns>
        public T GetLogger<T>(string name) where T : Logger
        {
            return (T)GetLogger(new LoggerCacheKey(name, typeof(T)));
        }

        /// <summary>
        /// Gets the specified named logger.  Use <paramref name="loggerType"/> to pass the type of the needed Logger.
        /// </summary>
        /// <param name="name">Name of the logger.</param>
        /// <param name="loggerType">The type of the logger to create. The type must inherit from <see cref="Logger" />.</param>
        /// <returns>The logger of type <paramref name="loggerType"/>. Multiple calls to <c>GetLogger</c> with the 
        /// same argument aren't guaranteed to return the same logger reference.</returns>
        public Logger GetLogger(string name, Type loggerType)
        {
            return GetLogger(new LoggerCacheKey(name, loggerType));
        }

        /// <summary>
        /// Loops through all loggers previously returned by GetLogger and recalculates their 
        /// target and filter list. Useful after modifying the configuration programmatically
        /// to ensure that all loggers have been properly configured.
        /// </summary>
        public void ReconfigExistingLoggers()
        {
            IEnumerable<Logger> loggers;

            lock (_syncRoot)
            {
                if (_config != null)
                {
                    _config.InitializeAll();
                }

                loggers = _loggerCache.Loggers;
            }

            foreach (var logger in loggers)
            {
                logger.SetConfiguration(GetConfigurationForLogger(logger.Name, _config));
            }
        }

#if !SILVERLIGHT
        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets) with the default timeout of 15 seconds.
        /// </summary>
        public void Flush()
        {
            Flush(DefaultFlushTimeout);
        }

        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets).
        /// </summary>
        /// <param name="timeout">Maximum time to allow for the flush. Any messages after that time 
        /// will be discarded.</param>
        public void Flush(TimeSpan timeout)
        {
            try
            {
                AsyncHelpers.RunSynchronously(cb => Flush(cb, timeout));
            }
            catch (Exception ex)
            {
                InternalLogger.Error(ex, "Error with flush.");
                if (ex.MustBeRethrown())
                {
                    throw;
                }

            }
        }

        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets).
        /// </summary>
        /// <param name="timeoutMilliseconds">Maximum time to allow for the flush. Any messages 
        /// after that time will be discarded.</param>
        public void Flush(int timeoutMilliseconds)
        {
            Flush(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        }
#endif

        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets).
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation.</param>
        public void Flush(AsyncContinuation asyncContinuation)
        {
            Flush(asyncContinuation, TimeSpan.MaxValue);
        }

        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets).
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation.</param>
        /// <param name="timeoutMilliseconds">Maximum time to allow for the flush. Any messages 
        /// after that time will be discarded.</param>
        public void Flush(AsyncContinuation asyncContinuation, int timeoutMilliseconds)
        {
            Flush(asyncContinuation, TimeSpan.FromMilliseconds(timeoutMilliseconds));
        }

        /// <summary>
        /// Flush any pending log messages (in case of asynchronous targets).
        /// </summary>
        /// <param name="asyncContinuation">The asynchronous continuation.</param>
        /// <param name="timeout">Maximum time to allow for the flush. Any messages after that time will be discarded.</param>
        public void Flush(AsyncContinuation asyncContinuation, TimeSpan timeout)
        {
            try
            {
                InternalLogger.Trace("LogFactory.Flush({0})", timeout);

                var loggingConfiguration = Configuration;
                if (loggingConfiguration != null)
                {
                    loggingConfiguration.FlushAllTargets(AsyncHelpers.WithTimeout(asyncContinuation, timeout));
                }
                else
                {
                    asyncContinuation(null);
                }
            }
            catch (Exception ex)
            {
                if (ThrowExceptions)
                {
                    throw;
                }

                InternalLogger.Error(ex, "Error with flush.");
            }
        }

        /// <summary>
        /// Decreases the log enable counter and if it reaches -1 the logs are disabled.
        /// </summary>
        /// <remarks>
        /// Logging is enabled if the number of <see cref="ResumeLogging"/> calls is greater than 
        /// or equal to <see cref="SuspendLogging"/> calls.
        /// 
        /// This method was marked as obsolete on NLog 4.0 and it may be removed in a future release.
        /// </remarks>
        /// <returns>An object that implements IDisposable whose Dispose() method re-enables logging. 
        /// To be used with C# <c>using ()</c> statement.</returns>
        [Obsolete("Use SuspendLogging() instead. Marked obsolete on NLog 4.0")]
        public IDisposable DisableLogging()
        {
            return SuspendLogging();
        }

        /// <summary>
        /// Increases the log enable counter and if it reaches 0 the logs are disabled.
        /// </summary>
        /// <remarks>
        /// Logging is enabled if the number of <see cref="ResumeLogging"/> calls is greater than 
        /// or equal to <see cref="SuspendLogging"/> calls.
        /// 
        /// This method was marked as obsolete on NLog 4.0 and it may be removed in a future release.
        /// </remarks>
        [Obsolete("Use ResumeLogging() instead. Marked obsolete on NLog 4.0")]
        public void EnableLogging()
        {
            ResumeLogging();
        }

        /// <summary>
        /// Decreases the log enable counter and if it reaches -1 the logs are disabled.
        /// </summary>
        /// <remarks>
        /// Logging is enabled if the number of <see cref="ResumeLogging"/> calls is greater than 
        /// or equal to <see cref="SuspendLogging"/> calls.
        /// </remarks>
        /// <returns>An object that implements IDisposable whose Dispose() method re-enables logging. 
        /// To be used with C# <c>using ()</c> statement.</returns>
        public IDisposable SuspendLogging()
        {
            lock (_syncRoot)
            {
                _logsEnabled--;
                if (_logsEnabled == -1)
                {
                    ReconfigExistingLoggers();
                }
            }

            return new LogEnabler(this);
        }

        /// <summary>
        /// Increases the log enable counter and if it reaches 0 the logs are disabled.
        /// </summary>
        /// <remarks>Logging is enabled if the number of <see cref="ResumeLogging"/> calls is greater 
        /// than or equal to <see cref="SuspendLogging"/> calls.</remarks>
        public void ResumeLogging()
        {
            lock (_syncRoot)
            {
                _logsEnabled++;
                if (_logsEnabled == 0)
                {
                    ReconfigExistingLoggers();
                }
            }
        }

        /// <summary>
        /// Returns <see langword="true" /> if logging is currently enabled.
        /// </summary>
        /// <returns>A value of <see langword="true" /> if logging is currently enabled, 
        /// <see langword="false"/> otherwise.</returns>
        /// <remarks>Logging is enabled if the number of <see cref="ResumeLogging"/> calls is greater 
        /// than or equal to <see cref="SuspendLogging"/> calls.</remarks>
        public bool IsLoggingEnabled()
        {
            return _logsEnabled >= 0;
        }

        /// <summary>
        /// Raises the event when the configuration is reloaded. 
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnConfigurationChanged(LoggingConfigurationChangedEventArgs e)
        {
            var changed = ConfigurationChanged;
            if (changed != null)
            {
                changed(this, e);
            }
        }

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        /// <summary>
        /// Raises the event when the configuration is reloaded. 
        /// </summary>
        /// <param name="e">Event arguments</param>
        protected virtual void OnConfigurationReloaded(LoggingConfigurationReloadedEventArgs e)
        {
            if (ConfigurationReloaded != null) ConfigurationReloaded.Invoke(this, e);
        }
#endif

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        internal void ReloadConfigOnTimer(object state)
        {
            if (reloadTimer == null && _isDisposing)
            {
                return; //timer was disposed already. 
            }

            LoggingConfiguration configurationToReload = (LoggingConfiguration)state;

            InternalLogger.Info("Reloading configuration...");
            lock (_syncRoot)
            {
                try
                {
                    if (_isDisposing)
                    {
                        return; //timer was disposed already. 
                    }

                    var currentTimer = reloadTimer;
                    if (currentTimer != null)
                    {
                        reloadTimer = null;
                        currentTimer.WaitForDispose(TimeSpan.Zero);
                    }

                    _watcher.StopWatching();

                    if (Configuration != configurationToReload)
                    {
                        throw new NLogConfigurationException("Config changed in between. Not reloading.");
                    }

                    LoggingConfiguration newConfig = configurationToReload.Reload();

                    //problem: XmlLoggingConfiguration.Initialize eats exception with invalid XML. ALso XmlLoggingConfiguration.Reload never returns null.
                    //therefor we check the InitializeSucceeded property.

                    var xmlConfig = newConfig as XmlLoggingConfiguration;
                    if (xmlConfig != null)
                    {
                        if (!xmlConfig.InitializeSucceeded.HasValue || !xmlConfig.InitializeSucceeded.Value)
                        {
                            throw new NLogConfigurationException("Configuration.Reload() failed. Invalid XML?");
                        }
                    }

                    if (newConfig != null)
                    {
                        if (KeepVariablesOnReload && _config != null)
                        {
                            newConfig.CopyVariables(_config.Variables);
                        }
                        Configuration = newConfig;
                        OnConfigurationReloaded(new LoggingConfigurationReloadedEventArgs(true));
                    }
                    else
                    {
                        throw new NLogConfigurationException("Configuration.Reload() returned null. Not reloading.");
                    }
                }
                catch (Exception exception)
                {
                    InternalLogger.Warn(exception, "NLog configuration while reloading");

                    if (exception.MustBeRethrownImmediately())
                    {
                        throw;  // Throwing exceptions here will crash the entire application (.NET 2.0 behavior)
                    }

                    _watcher.Watch(configurationToReload.FileNamesToWatch);
                    OnConfigurationReloaded(new LoggingConfigurationReloadedEventArgs(false, exception));
                }
            }
        }
#endif
        private void GetTargetsByLevelForLogger(string name, IEnumerable<LoggingRule> rules, TargetWithFilterChain[] targetsByLevel, TargetWithFilterChain[] lastTargetsByLevel, bool[] suppressedLevels)
        {
            //no "System.InvalidOperationException: Collection was modified"
            var loggingRules = new List<LoggingRule>(rules);
            foreach (LoggingRule rule in loggingRules)
            {
                if (!rule.NameMatches(name))
                {
                    continue;
                }

                for (int i = 0; i <= LogLevel.MaxLevel.Ordinal; ++i)
                {
                    if (i < GlobalThreshold.Ordinal || suppressedLevels[i] || !rule.IsLoggingEnabledForLevel(LogLevel.FromOrdinal(i)))
                    {
                        continue;
                    }

                    if (rule.Final)
                        suppressedLevels[i] = true;

                    foreach (Target target in rule.Targets.ToList())
                    {
                        var awf = new TargetWithFilterChain(target, rule.Filters);
                        if (lastTargetsByLevel[i] != null)
                        {
                            lastTargetsByLevel[i].NextInChain = awf;
                        }
                        else
                        {
                            targetsByLevel[i] = awf;
                        }

                        lastTargetsByLevel[i] = awf;
                    }
                }

                // Recursively analyze the child rules.
                GetTargetsByLevelForLogger(name, rule.ChildRules, targetsByLevel, lastTargetsByLevel, suppressedLevels);

            }

            for (int i = 0; i <= LogLevel.MaxLevel.Ordinal; ++i)
            {
                TargetWithFilterChain tfc = targetsByLevel[i];
                if (tfc != null)
                {
                    tfc.PrecalculateStackTraceUsage();
                }
            }
        }

        internal LoggerConfiguration GetConfigurationForLogger(string name, LoggingConfiguration configuration)
        {
            TargetWithFilterChain[] targetsByLevel = new TargetWithFilterChain[LogLevel.MaxLevel.Ordinal + 1];
            TargetWithFilterChain[] lastTargetsByLevel = new TargetWithFilterChain[LogLevel.MaxLevel.Ordinal + 1];
            bool[] suppressedLevels = new bool[LogLevel.MaxLevel.Ordinal + 1];

            if (configuration != null && IsLoggingEnabled())
            {
                GetTargetsByLevelForLogger(name, configuration.LoggingRules, targetsByLevel, lastTargetsByLevel, suppressedLevels);
            }

            if (InternalLogger.IsDebugEnabled)
            {
                InternalLogger.Debug("Targets for {0} by level:", name);
                for (int i = 0; i <= LogLevel.MaxLevel.Ordinal; ++i)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0} =>", LogLevel.FromOrdinal(i));
                    for (TargetWithFilterChain afc = targetsByLevel[i]; afc != null; afc = afc.NextInChain)
                    {
                        sb.AppendFormat(CultureInfo.InvariantCulture, " {0}", afc.Target.Name);
                        if (afc.FilterChain.Count > 0)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture, " ({0} filters)", afc.FilterChain.Count);
                        }
                    }

                    InternalLogger.Debug(sb.ToString());
                }
            }

#pragma warning disable 618
            return new LoggerConfiguration(targetsByLevel, configuration != null && configuration.ExceptionLoggingOldStyle);
#pragma warning restore 618
        }

        /// <summary>
        /// Currently this logfactory is disposing?
        /// </summary>
        private bool _isDisposing;

        private  void Close(TimeSpan flushTimeout)
        {
            if (_isDisposing)
            {
                return;
            }

            _isDisposing = true;

#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
            LoggerShutdown -= OnStopLogging;
            ConfigurationReloaded = null;   // Release event listeners

            if (_watcher != null)
            {
                // Disable startup of new reload-timers
                _watcher.FileChanged -= ConfigFileChanged;
            }
#endif

            if (Monitor.TryEnter(_syncRoot, 500))
            {
                try
                {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
                    var currentTimer = reloadTimer;
                    if (currentTimer != null)
                    {
                        reloadTimer = null;
                        currentTimer.WaitForDispose(TimeSpan.Zero);
                    }

                    if (_watcher != null)
                    {
                        // Dispose file-watcher after having dispose timer to avoid race
                        _watcher.Dispose();
                    }
#endif

                    var oldConfig = _config;
                    if (_configLoaded && oldConfig != null)
                    {
                        try
                        {
#if !SILVERLIGHT && !__IOS__ && !__ANDROID__ && !MONO
                            bool attemptClose = true;
                            if (flushTimeout != TimeSpan.Zero && !PlatformDetector.IsMono)
                            {
                                // MONO (and friends) have a hard time with spinning up flush threads/timers during shutdown (Maybe better with MONO 4.1)
                                ManualResetEvent flushCompleted = new ManualResetEvent(false);
                                oldConfig.FlushAllTargets((ex) => flushCompleted.Set());
                                attemptClose = flushCompleted.WaitOne(flushTimeout);
                            }
                            if (!attemptClose)
                            {
                                InternalLogger.Warn("Target flush timeout. One or more targets did not complete flush operation, skipping target close.");
                            }
                            else
#endif
                            {
                                // Flush completed within timeout, lets try and close down
                                oldConfig.Close();
                                _config = null;
                                OnConfigurationChanged(new LoggingConfigurationChangedEventArgs(null, oldConfig));
                            }
                        }
                        catch (Exception ex)
                        {
                            InternalLogger.Error(ex, "Error with close.");
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_syncRoot);
                }
            }

            ConfigurationChanged = null;    // Release event listeners
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>True</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Close(TimeSpan.Zero);
            }
        }

        internal void Shutdown()
        {
            InternalLogger.Info("Logger closing down...");
            if (!_isDisposing && _configLoaded)
            {
                var loadedConfig = Configuration;
                if (loadedConfig != null)
                {
                    ManualResetEvent flushCompleted = new ManualResetEvent(false);
                    loadedConfig.FlushAllTargets((ex) => flushCompleted.Set());
                    flushCompleted.WaitOne(DefaultFlushTimeout);
                    loadedConfig.Close();
                }
            }
            InternalLogger.Info("Logger has been closed down.");
        }

        /// <summary>
        /// Get file paths (including filename) for the possible NLog config files. 
        /// </summary>
        /// <returns>The filepaths to the possible config file</returns>
        public IEnumerable<string> GetCandidateConfigFilePaths()
        {
            if (_candidateConfigFilePaths != null)
            {
                return _candidateConfigFilePaths.AsReadOnly();
            }

            return GetDefaultCandidateConfigFilePaths();
        }

        /// <summary>
        /// Overwrite the paths (including filename) for the possible NLog config files.
        /// </summary>
        /// <param name="filePaths">The filepaths to the possible config file</param>
        public void SetCandidateConfigFilePaths(IEnumerable<string> filePaths)
        {
            _candidateConfigFilePaths = new List<string>();

            if (filePaths != null)
            {
                _candidateConfigFilePaths.AddRange(filePaths);
            }
        }
        /// <summary>
        /// Clear the candidate file paths and return to the defaults.
        /// </summary>
        public void ResetCandidateConfigFilePath()
        {
            _candidateConfigFilePaths = null;
        }

        /// <summary>
        /// Get default file paths (including filename) for possible NLog config files. 
        /// </summary>
        private static IEnumerable<string> GetDefaultCandidateConfigFilePaths()
        {
            // NLog.config from application directory
            if (CurrentAppDomain?.BaseDirectory != null)
            {
                yield return Path.Combine(CurrentAppDomain.BaseDirectory, "NLog.config");
                yield return Path.Combine(CurrentAppDomain.BaseDirectory, "nlog.config");
            }
            else
            {
                yield return "NLog.config";
                yield return "nlog.config";
            }

            // Current config file with .config renamed to .nlog
            string configurationFile = CurrentAppDomain?.ConfigurationFile;
            if (configurationFile != null)
            {
                yield return Path.ChangeExtension(configurationFile, ".nlog");

                // .nlog file based on the non-vshost version of the current config file
                const string vshostSubStr = ".vshost.";
                if (configurationFile.Contains(vshostSubStr))
                {
                    yield return Path.ChangeExtension(configurationFile.Replace(vshostSubStr, "."), ".nlog");
                }

                IEnumerable<string> privateBinPaths = CurrentAppDomain.PrivateBinPath;
                if (privateBinPaths != null)
                {
                    foreach (var path in privateBinPaths)
                    {
                        if (path != null)
                        {
                            yield return Path.Combine(path, "NLog.config");
                            yield return Path.Combine(path, "nlog.config");
                        }
                    }
                }
            }

#if !SILVERLIGHT && !NETSTANDARD1_5
            // Get path to NLog.dll.nlog only if the assembly is not in the GAC
            var nlogAssembly = typeof(LogFactory).Assembly;
            if (!nlogAssembly.GlobalAssemblyCache && !String.IsNullOrEmpty(nlogAssembly.Location))
            {
                yield return nlogAssembly.Location + ".nlog";
            }
#endif
        }

        private Logger GetLogger(LoggerCacheKey cacheKey)
        {
            lock (_syncRoot)
            {
                Logger existingLogger = _loggerCache.Retrieve(cacheKey);
                if (existingLogger != null)
                {
                    // Logger is still in cache and referenced.
                    return existingLogger;
                }

                Logger newLogger;

                if (cacheKey.ConcreteType != null && cacheKey.ConcreteType != typeof(Logger))
                {
                    var fullName = cacheKey.ConcreteType.FullName;
                    try
                    {

                        //creating instance of static class isn't possible, and also not wanted (it cannot inherited from Logger)
                        if (cacheKey.ConcreteType.IsStaticClass())
                        {
                            var errorMessage =
                                $"GetLogger / GetCurrentClassLogger is '{fullName}' as loggerType can be a static class and should inherit from Logger";
                            InternalLogger.Error(errorMessage);
                            if (ThrowExceptions)
                            {
                                throw new NLogRuntimeException(errorMessage);
                            }
                            newLogger = CreateDefaultLogger(ref cacheKey);
                        }
                        else
                        {

                            var instance = FactoryHelper.CreateInstance(cacheKey.ConcreteType);
                            newLogger = instance as Logger;
                            if (newLogger == null)
                            {
                                //well, it's not a Logger, and we should return a Logger.

                                var errorMessage =
                                    $"GetLogger / GetCurrentClassLogger got '{fullName}' as loggerType which doesn't inherit from Logger";
                                InternalLogger.Error(errorMessage);
                                if (ThrowExceptions)
                                {
                                    throw new NLogRuntimeException(errorMessage);
                                }

                                // Creating default instance of logger if instance of specified type cannot be created.
                                newLogger = CreateDefaultLogger(ref cacheKey);

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        InternalLogger.Error(ex, "GetLogger / GetCurrentClassLogger. Cannot create instance of type '{0}'. It should have an default contructor. ", fullName);

                        if (ex.MustBeRethrown())
                        {
                            throw;
                        }

                        // Creating default instance of logger if instance of specified type cannot be created.
                        newLogger = CreateDefaultLogger(ref cacheKey);
                    }
                }
                else
                {
                    newLogger = new Logger();
                }

                if (cacheKey.ConcreteType != null)
                {
                    newLogger.Initialize(cacheKey.Name, GetConfigurationForLogger(cacheKey.Name, Configuration), this);
                }

                // TODO: Clarify what is the intention when cacheKey.ConcreteType = null.
                //      At the moment, a logger typeof(Logger) will be created but the ConcreteType 
                //      will remain null and inserted into the cache. 
                //      Should we set cacheKey.ConcreteType = typeof(Logger) for default loggers?

                _loggerCache.InsertOrUpdate(cacheKey, newLogger);
                return newLogger;
            }
        }

        private static Logger CreateDefaultLogger(ref LoggerCacheKey cacheKey)
        {
            cacheKey = new LoggerCacheKey(cacheKey.Name, typeof(Logger));

            var newLogger = new Logger();
            return newLogger;
        }

        private void LoadLoggingConfiguration(string configFile)
        {
            InternalLogger.Debug("Loading config from {0}", configFile);
            _config = new XmlLoggingConfiguration(configFile, this);
        }


#if !SILVERLIGHT && !__IOS__ && !__ANDROID__
        private void ConfigFileChanged(object sender, EventArgs args)
        {
            InternalLogger.Info("Configuration file change detected! Reloading in {0}ms...", ReconfigAfterFileChangedTimeout);

            // In the rare cases we may get multiple notifications here, 
            // but we need to reload config only once.
            //
            // The trick is to schedule the reload in one second after
            // the last change notification comes in.
            lock (_syncRoot)
            {
                if (_isDisposing)
                {
                    return;
                }

                if (reloadTimer == null)
                {
                    var configuration = Configuration;
                    if (configuration != null)
                    {
                        reloadTimer = new Timer(
                                ReloadConfigOnTimer,
                                configuration,
                                ReconfigAfterFileChangedTimeout,
                                Timeout.Infinite);
                    }
                }
                else
                {
                    reloadTimer.Change(
                            ReconfigAfterFileChangedTimeout,
                            Timeout.Infinite);
                }
            }
        }
#endif
        /// <summary>
        /// Logger cache key.
        /// </summary>
        internal struct LoggerCacheKey : IEquatable<LoggerCacheKey>
        {
            public readonly string Name;
            public readonly Type ConcreteType;

            public LoggerCacheKey(string name, Type concreteType)
            {
                Name = name;
                ConcreteType = concreteType;
            }

            /// <summary>
            /// Serves as a hash function for a particular type.
            /// </summary>
            /// <returns>
            /// A hash code for the current <see cref="T:System.Object"/>.
            /// </returns>
            public override int GetHashCode()
            {
                return ConcreteType.GetHashCode() ^ Name.GetHashCode();
            }

            /// <summary>
            /// Determines if two objects are equal in value.
            /// </summary>
            /// <param name="obj">Other object to compare to.</param>
            /// <returns>True if objects are equal, false otherwise.</returns>
            public override bool Equals(object obj)
            {
                return obj is LoggerCacheKey && Equals((LoggerCacheKey)obj);
            }

            /// <summary>
            /// Determines if two objects of the same type are equal in value.
            /// </summary>
            /// <param name="key">Other object to compare to.</param>
            /// <returns>True if objects are equal, false otherwise.</returns>
            public bool Equals(LoggerCacheKey key)
            {
                return (ConcreteType == key.ConcreteType) && string.Equals(key.Name, Name, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Logger cache.
        /// </summary>
        private class LoggerCache
        {
            // The values of WeakReferences are of type Logger i.e. Directory<LoggerCacheKey, Logger>.
            private readonly Dictionary<LoggerCacheKey, WeakReference> _loggerCache =
                    new Dictionary<LoggerCacheKey, WeakReference>();

            /// <summary>
            /// Inserts or updates. 
            /// </summary>
            /// <param name="cacheKey"></param>
            /// <param name="logger"></param>
            public void InsertOrUpdate(LoggerCacheKey cacheKey, Logger logger)
            {
                _loggerCache[cacheKey] = new WeakReference(logger);
            }

            public Logger Retrieve(LoggerCacheKey cacheKey)
            {
                WeakReference loggerReference;
                if (_loggerCache.TryGetValue(cacheKey, out loggerReference))
                {
                    // logger in the cache and still referenced
                    return loggerReference.Target as Logger;
                }

                return null;
            }

            public IEnumerable<Logger> Loggers => GetLoggers();

            private IEnumerable<Logger> GetLoggers()
            {
                // TODO: Test if loggerCache.Values.ToList<Logger>() can be used for the conversion instead.
                List<Logger> values = new List<Logger>(_loggerCache.Count);

                foreach (WeakReference loggerReference in _loggerCache.Values)
                {
                    Logger logger = loggerReference.Target as Logger;
                    if (logger != null)
                    {
                        values.Add(logger);
                    }
                }

                return values;
            }
        }

        /// <summary>
        /// Enables logging in <see cref="IDisposable.Dispose"/> implementation.
        /// </summary>
        private class LogEnabler : IDisposable
        {
            private LogFactory _factory;

            /// <summary>
            /// Initializes a new instance of the <see cref="LogEnabler" /> class.
            /// </summary>
            /// <param name="factory">The factory.</param>
            public LogEnabler(LogFactory factory)
            {
                _factory = factory;
            }

            /// <summary>
            /// Enables logging.
            /// </summary>
            void IDisposable.Dispose()
            {
                _factory.ResumeLogging();
            }
        }

        private static void RegisterEvents(IAppDomain appDomain)
        {
            if (appDomain == null) return;

            try
            {
                appDomain.ProcessExit += OnLoggerShutdown;
                appDomain.DomainUnload += OnLoggerShutdown;
            }
            catch (Exception exception)
            {
                InternalLogger.Warn(exception, "Error setting up termination events.");

                if (exception.MustBeRethrown())
                {
                    throw;
                }
            }
        }

        private static void UnregisterEvents(IAppDomain appDomain)
        {
            if (appDomain == null) return;

            appDomain.DomainUnload -= OnLoggerShutdown;
            appDomain.ProcessExit -= OnLoggerShutdown;
        }

        private static void OnLoggerShutdown(object sender, EventArgs args)
        {
            try
            {
                var loggerShutdown = LoggerShutdown;
                if (loggerShutdown != null)
                    loggerShutdown.Invoke(sender, args);
            }
            catch (Exception ex)
            {
                if (ex.MustBeRethrownImmediately())
                    throw;
                InternalLogger.Error(ex, "LogFactory failed to shut down properly.");
            }
            finally
            {
                LoggerShutdown = null;
                if (currentAppDomain != null)
                {
                    CurrentAppDomain = null;    // Unregister and disconnect from AppDomain
                }
            }
        }

        private void OnStopLogging(object sender, EventArgs args)
        {
            try
            {
                //stop timer on domain unload, otherwise: 
                //Exception: System.AppDomainUnloadedException
                //Message: Attempted to access an unloaded AppDomain.
                InternalLogger.Info("Shutting down logging...");
                // Finalizer thread has about 2 secs, before being terminated
                Close(TimeSpan.FromMilliseconds(1500));
                InternalLogger.Info("Logger has been shut down.");
            }
            catch (Exception ex)
            {
                if (ex.MustBeRethrownImmediately())
                    throw;
                InternalLogger.Error(ex, "Logger failed to shut down properly.");
            }
        }
    }
}
