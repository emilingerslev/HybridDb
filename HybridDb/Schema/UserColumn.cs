﻿using System;

namespace HybridDb.Schema
{
    public class UserColumn : Column
    {
        public UserColumn(string name, SqlColumn sqlColumn)
        {
            Name = name;
            SqlColumn = sqlColumn;
        }

        public UserColumn(string name, Type type)
        {
            Name = name;
            SqlColumn = new SqlColumn(type);
        }

        public UserColumn(string name)
        {
            Name = name;
            SqlColumn = new SqlColumn();
        }
    }

    public class CollectionColumn : Column
    {
        public CollectionColumn(string columnName)
        {
            Name = columnName;
            SqlColumn = new SqlColumn(typeof(int));
        }
    }
}