﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ResponsivePath.Logging
{
    /// <summary>
    /// A basic logger. 
    /// </summary>
    public class Logger : ILogger
    {
        private ILogConfiguration config;

        /// <summary>
        /// Constructor accepting a log configuration
        /// </summary>
        /// <param name="config">The configuration, including accumulators, indexers, and recorders.</param>
        public Logger(ILogConfiguration config)
        {
            this.config = config;
        }

        async Task ILogger.Record(LogEntry logEntry)
        {
            foreach (var accumulator in config.Accumulators)
            {
                try
                {
                    accumulator.AccumulateData(logEntry);
                }
                catch { }
            }
            var logTask = Task.Run(async () =>
            {
                foreach (var indexer in config.Indexers)
                {
                    try
                    {
                        await indexer.IndexData(logEntry).ConfigureAwait(false);
                    }
                    catch { }
                }
                try
                {
                    await Task.WhenAll(from recorder in config.Recorders
                                       select recorder.Save(logEntry)).ConfigureAwait(false);
                }
                catch { }
            });
            if (config.WaitForLogRecording)
            {
                await logTask.ConfigureAwait(false);
            }
        }
    }
}
