﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Transactions;
using Dapper;
using HybridDb.Commands;
using HybridDb.Logging;
using HybridDb.Schema;

namespace HybridDb
{
    public class DocumentStore : IDocumentStore
    {
        readonly Configuration configuration;
        readonly string connectionString;
        readonly TableMode tableMode;

        DbConnection ambientConnectionForTesting;
        Guid lastWrittenEtag;
        long numberOfRequests;
        int numberOfManagedConnections;

        DocumentStore(string connectionString, TableMode tableMode)
        {
            this.tableMode = tableMode;
            this.connectionString = connectionString;

            configuration = new Configuration();
        }

        public DocumentStore(string connectionString) : this(connectionString, TableMode.UseRealTables) {}

        public TableMode TableMode
        {
            get { return tableMode; }
        }

        public bool IsInTestMode
        {
            get { return tableMode != TableMode.UseRealTables; }
        }

        public void Dispose()
        {
            if (numberOfManagedConnections > 0)
                Logger.Warn("A ManagedConnection was not properly disposed. You may be leaking sql connections or transactions.");

            if (ambientConnectionForTesting != null)
                ambientConnectionForTesting.Dispose();
        }

        /// <summary>
        /// Configuration for tables and projections used in sessions.
        /// The store does not use the configuration in itself.
        /// </summary>
        public Configuration Configuration
        {
            get { return configuration; }
        }

        public IMigrator CreateMigrator()
        {
            return new Migrator(this);
        }

        public void Migrate(Action<IMigrator> migration)
        {
            using (var migrator = CreateMigrator())
            {
                migration(migrator);
                migrator.Commit();
            }
        }

        public void InitializeDatabase(bool safe = true)
        {
            Migrate(migrator =>
            {
                foreach (var table in Configuration.Tables)
                {
                    migrator.MigrateTo(table, safe);
                }
            });
        }

        public DocumentConfiguration<TEntity> DocumentsFor<TEntity>()
        {
            return DocumentsFor<TEntity>(null);
        }

        public DocumentConfiguration<TEntity> DocumentsFor<TEntity>(string name)
        {
            var table = new Table(name ?? Configuration.GetTableNameByConventionFor<TEntity>());
            var relation = new DocumentConfiguration<TEntity>(Configuration, table);
            configuration.Register(relation);
            return relation;
        }

        public static DocumentStore ForTestingWithGlobalTempTables(string connectionString = null)
        {
            return new DocumentStore(connectionString ?? "data source=.;Integrated Security=True", TableMode.UseGlobalTempTables);
        }

        public static DocumentStore ForTestingWithTempTables(string connectionString = null)
        {
            return new DocumentStore(connectionString ?? "data source=.;Integrated Security=True", TableMode.UseTempTables);
        }

        public IDocumentSession OpenSession()
        {
            return new DocumentSession(this);
        }

        public Guid Execute(params DatabaseCommand[] commands)
        {
            if (commands.Length == 0)
                throw new ArgumentException("No commands were passed");

            var timer = Stopwatch.StartNew();
            using (var connectionManager = Connect())
            {
                var i = 0;
                var etag = Guid.NewGuid();
                var sql = "";
                var parameters = new List<Parameter>();
                var numberOfParameters = 0;
                var expectedRowCount = 0;
                var numberOfInsertCommands = 0;
                var numberOfUpdateCommands = 0;
                var numberOfDeleteCommands = 0;
                foreach (var command in commands)
                {
                    if (command is InsertCommand)
                        numberOfInsertCommands++;

                    if (command is UpdateCommand)
                        numberOfUpdateCommands++;

                    if (command is DeleteCommand)
                        numberOfDeleteCommands++;

                    var preparedCommand = command.Prepare(this, etag, i++);
                    var numberOfNewParameters = preparedCommand.Parameters.Count;

                    if (numberOfNewParameters >= 2100)
                        throw new InvalidOperationException("Cannot execute a query with more than 2100 parameters.");

                    if (numberOfParameters + numberOfNewParameters >= 2100)
                    {
                        InternalExecute(connectionManager, sql, parameters, expectedRowCount);

                        sql = "";
                        parameters = new List<Parameter>();
                        expectedRowCount = 0;
                        numberOfParameters = 0;
                    }

                    expectedRowCount += preparedCommand.ExpectedRowCount;
                    numberOfParameters += numberOfNewParameters;

                    sql += string.Format("{0};", preparedCommand.Sql);
                    parameters.AddRange(preparedCommand.Parameters);
                }

                InternalExecute(connectionManager, sql, parameters, expectedRowCount);

                connectionManager.Complete();

                Logger.Info("Executed {0} inserts, {1} updates and {2} deletes in {3}ms",
                            numberOfInsertCommands,
                            numberOfUpdateCommands,
                            numberOfDeleteCommands,
                            timer.ElapsedMilliseconds);

                lastWrittenEtag = etag;
                return etag;
            }
        }

        public Guid Insert(Table table, Guid key, byte[] document, object projections)
        {
            return Execute(new InsertCommand(table, key, document, projections));
        }

        public Guid Update(Table table, Guid key, Guid etag, byte[] document, object projections, bool lastWriteWins = false)
        {
            return Execute(new UpdateCommand(table, key, etag, document, projections, lastWriteWins));
        }

        public void Delete(Table table, Guid key, Guid etag, bool lastWriteWins = false)
        {
            Execute(new DeleteCommand(table, key, etag, lastWriteWins));
        }

        public IEnumerable<TProjection> Query<TProjection>(Table table, out QueryStats stats, string select = null, string where = "",
                                                           int skip = 0, int take = 0, string orderby = "", object parameters = null)
        {
            if (select.IsNullOrEmpty() || select == "*")
                select = "";

            var isTypedProjection = !typeof (TProjection).IsA<IDictionary<Column, object>>();
            if (isTypedProjection)
                select = MatchSelectedColumnsWithProjectedType<TProjection>(select);

            var timer = Stopwatch.StartNew();
            using (var connection = Connect())
            {
                var isWindowed = skip > 0 || take > 0;
                var rowNumberOrderBy = string.IsNullOrEmpty(@orderby) ? "CURRENT_TIMESTAMP" : @orderby;

                var sql = new SqlBuilder();
                sql.Append(@"with temp as (select {0}", select.IsNullOrEmpty() ? "*" : select)
                   .Append(isWindowed, ", row_number() over(ORDER BY {0}) as RowNumber", rowNumberOrderBy)
                   .Append("from {0}", FormatTableNameAndEscape(table.Name))
                   .Append(!string.IsNullOrEmpty(@where), "where {0}", @where)
                   .Append(")")
                   .Append("select *, (select count(*) from temp) as TotalResults from temp")
                   .Append(isWindowed, "where RowNumber >= {0}", skip + 1)
                   .Append(isWindowed && take > 0, "and RowNumber <= {0}", skip + take)
                   .Append(isWindowed, "order by RowNumber")
                   .Append(!isWindowed && !string.IsNullOrEmpty(orderby), "order by {0}", orderby);

                var result = isTypedProjection
                                 ? InternalQuery<TProjection>(connection, sql, parameters, out stats)
                                 : (IEnumerable<TProjection>) (InternalQuery<object>(connection, sql, parameters, out stats)
                                                                  .Cast<IDictionary<string, object>>()
                                                                  .Select(row => row.ToDictionary(
                                                                      column => table.GetColumnOrDefaultDynamicColumn(column.Key, column.Value.GetTypeOrDefault()),
                                                                      column => column.Value)));

                Interlocked.Increment(ref numberOfRequests);
                Logger.Info("Retrieved {0} in {1}ms", "", timer.ElapsedMilliseconds);

                connection.Complete();
                return result;
            }
        }

        public IEnumerable<IDictionary<Column, object>> Query(Table table, out QueryStats stats, string select = null, string where = "",
                                                              int skip = 0, int take = 0, string orderby = "", object parameters = null)
        {
            return Query<IDictionary<Column, object>>(table, out stats, select, @where, skip, take, orderby, parameters);
        }

        public IDictionary<Column, object> Get(Table table, Guid key)
        {
            var timer = Stopwatch.StartNew();
            using (var connection = Connect())
            {
                var sql = string.Format("select * from {0} where {1} = @Id",
                                        FormatTableNameAndEscape(table.Name),
                                        table.IdColumn.Name);

                var row = ((IDictionary<string, object>) connection.Connection.Query(sql, new {Id = key}).SingleOrDefault());

                Interlocked.Increment(ref numberOfRequests);

                Logger.Info("Retrieved {0} in {1}ms", key, timer.ElapsedMilliseconds);

                connection.Complete();
                return row != null ? row.ToDictionary(x => table.GetColumnOrDefaultDynamicColumn(x.Key, x.Value.GetTypeOrDefault()), x => x.Value) : null;
            }
        }

        public long NumberOfRequests
        {
            get { return numberOfRequests; }
        }

        public Guid LastWrittenEtag
        {
            get { return lastWrittenEtag; }
        }

        public string FormatTableNameAndEscape(string tablename)
        {
            return Escape(FormatTableName(tablename));
        }

        public string Escape(string identifier)
        {
            return string.Format("[{0}]", identifier);
        }

        public string FormatTableName(string tablename)
        {
            switch (tableMode)
            {
                case TableMode.UseRealTables:
                    return tablename;
                case TableMode.UseTempTables:
                    return "#" + tablename;
                case TableMode.UseGlobalTempTables:
                    return "##" + tablename;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        ILogger Logger
        {
            get { return Configuration.Logger; }
        }

        internal ManagedConnection Connect()
        {
            Action complete = () => { };
            Action dispose = () => { numberOfManagedConnections--; };

            try
            {
                if (Transaction.Current == null)
                {
                    var tx = new TransactionScope();
                    complete += tx.Complete;
                    dispose += tx.Dispose;
                }

                DbConnection connection;
                if (IsInTestMode)
                {
                    // We don't care about thread safety in test mode
                    if (ambientConnectionForTesting == null)
                    {
                        ambientConnectionForTesting = new SqlConnection(connectionString);
                        ambientConnectionForTesting.Open();
                    }

                    connection = ambientConnectionForTesting;
                }
                else
                {
                    connection = new SqlConnection(connectionString);
                    connection.Open();

                    complete = connection.Dispose + complete;
                    dispose = connection.Dispose + dispose;
                }

                // Connections that are kept open during multiple operations (for testing mostly)
                // will not automatically be enlisted in transactions started later, we fix that here.
                // Calling EnlistTransaction on a connection that is already enlisted is a no-op.
                connection.EnlistTransaction(Transaction.Current);

                numberOfManagedConnections++;

                return new ManagedConnection(connection, complete, dispose);
            }
            catch (Exception)
            {
                dispose();
                throw;
            }
        }

        internal IEnumerable<T> RawQuery<T>(string sql, object parameters = null)
        {
            using (var connection = Connect())
            {
                return connection.Connection.Query<T>(sql, parameters);
            }
        }

        internal IEnumerable<dynamic> RawQuery(string sql, object parameters = null)
        {
            using (var connection = Connect())
            {
                return connection.Connection.Query(sql, parameters);
            }
        }

        static string MatchSelectedColumnsWithProjectedType<TProjection>(string select)
        {
            var neededColumns = typeof (TProjection).GetProperties().Select(x => x.Name).ToList();
            var selectedColumns = from clause in @select.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                                  let split = Regex.Split(clause, " AS ", RegexOptions.IgnoreCase).Where(x => x != "").ToArray()
                                  let column = split[0]
                                  let alias = split.Length > 1 ? split[1] : null
                                  where neededColumns.Contains(alias)
                                  select new {column, alias = alias ?? column};

            var missingColumns = from column in neededColumns
                                 where !selectedColumns.Select(x => x.alias).Contains(column)
                                 select new {column, alias = column};

            select = string.Join(", ", selectedColumns.Union(missingColumns).Select(x => x.column + " AS " + x.alias));
            return select;
        }

        IEnumerable<T> InternalQuery<T>(ManagedConnection connection, SqlBuilder sql, object parameters, out QueryStats metadata)
        {
            var normalizedParameters = parameters as IEnumerable<Parameter> ??
                                       (from projection in (parameters as IDictionary<string, object> ?? ObjectToDictionaryRegistry.Convert(parameters))
                                        select new Parameter {Name = "@" + projection.Key, Value = projection.Value}).ToList();

            var rows = connection.Connection.Query<T, QueryStats, Tuple<T, QueryStats>>(sql.ToString(), Tuple.Create,
                                                                                        new FastDynamicParameters(normalizedParameters),
                                                                                        splitOn: "TotalResults");

            var firstRow = rows.FirstOrDefault();
            metadata = firstRow != null ? new QueryStats {TotalResults = firstRow.Item2.TotalResults} : new QueryStats();

            Interlocked.Increment(ref numberOfRequests);

            return rows.Select(x => x.Item1);
        }

        void InternalExecute(ManagedConnection managedConnection, string sql, List<Parameter> parameters, int expectedRowCount)
        {
            var fastParameters = new FastDynamicParameters(parameters);
            var rowcount = managedConnection.Connection.Execute(sql, fastParameters);
            Interlocked.Increment(ref numberOfRequests);
            if (rowcount != expectedRowCount)
                throw new ConcurrencyException();
        }

        public class MissingProjectionValueException : Exception {}

        public bool CanConnect()
        {
            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public enum TableMode
    {
        UseRealTables,
        UseTempTables,
        UseGlobalTempTables
    }
}