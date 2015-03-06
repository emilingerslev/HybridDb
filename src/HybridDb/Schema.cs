﻿using System;
using System.Collections.Generic;
using System.Linq;
using HybridDb.Config;

namespace HybridDb
{
    public class Schema : ISchema
    {
        readonly DocumentStore store;
        readonly TableMode tableMode;

        public Schema(DocumentStore store, TableMode tableMode)
        {
            this.store = store;
            this.tableMode = tableMode;
        }

        public bool TableExists(string name)
        {
            if (tableMode == TableMode.UseRealTables)
            {
                return store.RawQuery<dynamic>(string.Format("select OBJECT_ID('{0}') as Result", name)).First().Result != null;
            }
        
            return store.RawQuery<dynamic>(string.Format("select OBJECT_ID('tempdb..{0}') as Result", store.FormatTableName(name))).First().Result != null;
        }

        public List<string> GetTables()
        {
            return tableMode == TableMode.UseRealTables
                ? store.RawQuery<string>("select table_name from information_schema.tables where table_type='BASE TABLE'").ToList()
                : store.RawQuery<string>("select * from tempdb.sys.objects where object_id('tempdb.dbo.' + name, 'U') is not null AND name LIKE '#%'")
                    .ToList();
        }

        public Column GetColumn(string table, string column)
        {
            Column2 c;
            if (tableMode == TableMode.UseRealTables)
            {
                c = store.RawQuery<Column2>(
                    string.Format(
                        "select * from sys.columns where Name = N'{0}' and Object_ID = Object_ID(N'{1}')", column,
                        table)).FirstOrDefault();
            }
            else
            {
                c = store.RawQuery<Column2>(
                        string.Format(
                            "select * from tempdb.sys.columns where Name = N'{0}' and Object_ID = Object_ID(N'tempdb..{1}')",
                            column, store.FormatTableName(table))).FirstOrDefault();
                
            }

            if (c == null)
                return null;

            var rawQuery = store.RawQuery<string>("select name from sys.types where system_type_id = @id", new { c.system_type_id });

            return null;
        }

        public class Column2
        {
            public string Name { get; set; }
            public int system_type_id { get; set; }
            public int max_length { get; set; }
        }
    }
}