﻿using System;
using System.Collections.Generic;

namespace HybridDb.Schema
{
    public interface ITable
    {
        string Name { get; }
        IEnumerable<IColumn> Columns { get; }
        IdColumn IdColumn { get; }
        EtagColumn EtagColumn { get; }
        DocumentColumn DocumentColumn { get; }
        Type EntityType { get; }
        IColumn this[string name] { get; }
    }
}