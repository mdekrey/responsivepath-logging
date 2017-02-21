using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Data.Common;
using DeKreyConsulting.AdoTestability;

namespace ResponsivePath.Logging
{
    /// <summary>
    /// A log recorder that writes to a sql database.
    /// </summary>
    /// <remarks>
    /// Commands in the log recorder expect the connection string to default to a database that matches the dacpac.
    /// </remarks>
    public class SqlLogRecorder : ILogRecorder
    {
        /// <summary>
        /// Inserts a new log entry
        /// </summary>
        public static CommandBuilder cmdCreateEntry = new CommandBuilder(@"INSERT INTO Entries (Timestamp, Severity, Message, Exception, Data)
                                                                           VALUES (@timestamp, @severity, @message, @exception, @data);
                                                                           SELECT SCOPE_IDENTITY();", 
            new Dictionary<string, Action<DbParameter>>
            {
                { "@timestamp", param => param.DbType = System.Data.DbType.DateTime2 },
                { "@severity", param => param.DbType = System.Data.DbType.String },
                { "@message", param => param.DbType = System.Data.DbType.String },
                { "@exception", param => param.DbType = System.Data.DbType.String },
                { "@data", param => param.DbType = System.Data.DbType.String },
            });
        /// <summary>
        /// Inserts an index value
        /// </summary>
        public static CommandBuilder cmdCreateIndex = new CommandBuilder(@"INSERT INTO EntryIndexes ([EntryId], [Key], [Value])
                                                                           VALUES (@entryid, @key, @value);",
            new Dictionary<string, Action<DbParameter>>
            {
                { "@entryid", param => param.DbType = System.Data.DbType.Int32 },
                { "@key", param => param.DbType = System.Data.DbType.String },
                { "@value", param => param.DbType = System.Data.DbType.String },
            });

        private readonly DbProviderFactory providerFactory;
        private readonly string connectionString;
        private readonly Severity minSeverity;
        private readonly IList<string> pathsToIgnore;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="connectionString">Specifies the connection string for the sql connection</param>
        /// <param name="minSeverity">The minimum severity to log to the database.</param>
        /// <param name="pathsToIgnore">JSON paths to ignore, rooted inside Exception and Data. Useful for not logging SSNs, card numbers, and other sensitive 
        /// information that might be nested in logged objects.</param>
        /// <param name="providerFactory">The database provider factory. Should be either SqlClient or a mock.</param>
        public SqlLogRecorder(string connectionString, Severity minSeverity, IEnumerable<string> pathsToIgnore, DbProviderFactory providerFactory = null)
        {
            this.providerFactory = providerFactory ?? SqlClientFactory.Instance;
            this.connectionString = connectionString;
            this.minSeverity = minSeverity;
            this.pathsToIgnore = pathsToIgnore.ToList().AsReadOnly();
        }

        async Task ILogRecorder.Save(LogEntry logEntry)
        {
            if (logEntry.Severity < minSeverity)
            {
                return;
            }

            using (var connection = providerFactory.CreateConnection(connectionString))
            using (var cmdCreateEntry = SqlLogRecorder.cmdCreateEntry.BuildFrom(connection, new Dictionary<string, object>
            {
                { "@timestamp", logEntry.Timestamp.DateTime },
                { "@severity", logEntry.Severity.ToString("g") },
                { "@message", logEntry.Message ?? (object)DBNull.Value },
                { "@exception", JsonEncode(logEntry.Exception) ?? (object)DBNull.Value },
                { "@data", JsonEncode(logEntry.Data) ?? (object)DBNull.Value },
            }))
            using (var cmdCreateIndex = SqlLogRecorder.cmdCreateIndex.BuildFrom(connection))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                var entryId = Convert.ToInt32(await cmdCreateEntry.ExecuteScalarAsync().ConfigureAwait(false));

                cmdCreateIndex.ApplyParameters(new Dictionary<string, object> { { "@entryid", entryId } });
                foreach (var index in from key in logEntry.Indexes.AllKeys
                                      from value in logEntry.Indexes.GetValues(key)
                                      select new { key, value })
                {
                    cmdCreateIndex.ApplyParameters(new Dictionary<string, object>
                    {
                        { "@key", index.key },
                        { "@value", index.value },
                    });

                    await cmdCreateIndex.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private string JsonEncode(object data)
        {
            if (data == null)
                return null;

            if ((pathsToIgnore?.Count ?? 0) > 0)
            {
                var jsonTokenized = Newtonsoft.Json.Linq.JToken.FromObject(data);
                foreach (var path in pathsToIgnore)
                {
                    try
                    {
                        var values = jsonTokenized.SelectTokens(path, false).ToArray();
                        foreach (var value in values)
                        {
                            value.Parent.Remove();
                        }
                    }
                    catch { }
                }
                return jsonTokenized.ToString();
            }
            else
            {
                return JsonConvert.SerializeObject(data);
            }
        }

        private class HidePropertiesContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
        {
            private IList<string> _propertiesToIgnore = null;

            public HidePropertiesContractResolver(IList<string> propertiesToIgnore)
            {
                _propertiesToIgnore = propertiesToIgnore;
            }

            protected override IList<JsonProperty> CreateProperties(Type type, Newtonsoft.Json.MemberSerialization memberSerialization)
            {
                IList<JsonProperty> properties = base.CreateProperties(type, memberSerialization);
                if (_propertiesToIgnore != null)
                {
                    return properties.Where(p => !_propertiesToIgnore.Contains(p.PropertyName, StringComparer.InvariantCultureIgnoreCase)).ToList();
                }
                else
                {
                    return properties;
                }
            }
        }
    }
}
