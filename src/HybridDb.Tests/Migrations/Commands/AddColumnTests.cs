using System;
using HybridDb.Config;
using HybridDb.Migrations.Commands;
using Shouldly;
using Xunit;
using Xunit.Extensions;

namespace HybridDb.Tests.Migrations.Commands
{
    public class AddColumnTests : HybridDbStoreTests
    {
        [Theory]
        [InlineData(TableMode.UseTempTables)]
        [InlineData(TableMode.UseTempDb)]
        [InlineData(TableMode.UseRealTables)]
        public void AddsColumn(TableMode mode)
        {
            Use(mode);
            new CreateTable(new Table("Entities", new Column("Col1", typeof(int)))).Execute(database);

            new AddColumn("Entities", new Column("Col2", typeof(int))).Execute(database);

            database.QuerySchema()["Entities"]["Col2"].ShouldNotBe(null);
        }

        [Theory]
        [InlineData(TableMode.UseTempTables, typeof(int), false)]
        [InlineData(TableMode.UseTempDb, typeof(int), false)]
        [InlineData(TableMode.UseRealTables, typeof(int), false)]
        [InlineData(TableMode.UseTempTables, typeof(double), false)]
        [InlineData(TableMode.UseTempDb, typeof(double), false)]
        [InlineData(TableMode.UseRealTables, typeof(double), false)]
        [InlineData(TableMode.UseTempTables, typeof(string), true)]
        [InlineData(TableMode.UseTempDb, typeof(string), true)]
        [InlineData(TableMode.UseRealTables, typeof(string), true)]
        public void ColumnIsOfCorrectType(TableMode mode, Type type, bool nullable)
        {
            Use(TableMode.UseRealTables);
            new CreateTable(new Table("Entities", new Column("Col1", typeof(int)))).Execute(database);

            new AddColumn("Entities", new Column("Col2", type)).Execute(database);

            database.QuerySchema()["Entities"]["Col2"].Type.ShouldBe(type);
            database.QuerySchema()["Entities"]["Col2"].Nullable.ShouldBe(nullable);
        }

        [Theory]
        [InlineData(TableMode.UseTempTables)]
        [InlineData(TableMode.UseTempDb)]
        [InlineData(TableMode.UseRealTables)]
        public void SetsColumnAsNullableAndUsesUnderlyingTypeWhenNullable(TableMode mode)
        {
            Use(mode);
            new CreateTable(new Table("Entities", new Column("Col1", typeof(int)))).Execute(database);

            new AddColumn("Entities", new Column("Col2", typeof(int?))).Execute(database);

            database.QuerySchema()["Entities"]["Col2"].Type.ShouldBe(typeof(int));
            database.QuerySchema()["Entities"]["Col2"].Nullable.ShouldBe(true);
        }

        [Theory]
        [InlineData(TableMode.UseTempTables)]
        [InlineData(TableMode.UseTempDb)]
        [InlineData(TableMode.UseRealTables)]
        public void CanSetColumnAsPrimaryKey(TableMode mode)
        {
            Use(mode);

            new CreateTable(new Table("Entities1", new Column("test", typeof(int)))).Execute(database);
            new AddColumn("Entities1", new Column("SomeInt", typeof(int), isPrimaryKey: true)).Execute(database);

            database.QuerySchema()["Entities1"]["SomeInt"].IsPrimaryKey.ShouldBe(true);
        }

        [Theory]
        [InlineData(TableMode.UseTempTables)]
        [InlineData(TableMode.UseTempDb)]
        [InlineData(TableMode.UseRealTables)]
        public void CanAddColumnWithDefaultValue(TableMode mode)
        {
            Use(mode);
            new CreateTable(new Table("Entities1", new Column("test", typeof(int)))).Execute(database);

            new AddColumn("Entities1", new Column("SomeNullableInt", typeof(int?), defaultValue: null)).Execute(database);
            new AddColumn("Entities1", new Column("SomeOtherNullableInt", typeof(int?), defaultValue: 42)).Execute(database);
            new AddColumn("Entities1", new Column("SomeString", typeof(string), defaultValue: "peter")).Execute(database);
            new AddColumn("Entities1", new Column("SomeInt", typeof(int),  defaultValue: 666)).Execute(database);
            new AddColumn("Entities1", new Column("SomeDateTime", typeof(DateTime),  defaultValue: new DateTime(1999, 12, 24))).Execute(database);

            var schema = database.QuerySchema();

            schema["Entities1"]["SomeNullableInt"].DefaultValue.ShouldBe(null);
            schema["Entities1"]["SomeOtherNullableInt"].DefaultValue.ShouldBe(42);
            schema["Entities1"]["SomeString"].DefaultValue.ShouldBe("peter");
            schema["Entities1"]["SomeInt"].DefaultValue.ShouldBe(666);
            schema["Entities1"]["SomeDateTime"].DefaultValue.ShouldBe(new DateTime(1999, 12, 24));
        }

        [Fact(Skip = "Not solved yet")]
        public void ShouldNotAllowSqlInjection()
        {
            new CreateTable(new Table("Entities1", new Column("test", typeof(int)))).Execute(database);
            new AddColumn("Entities1", new Column("SomeString", typeof(string), defaultValue: "'; DROP TABLE #Entities1; SELECT '")).Execute(database);

            database.QuerySchema().ShouldContainKey("Entities1");
        }

        [Fact]
        public void IsSafe()
        {
            new AddColumn("Entities", new Column("Col", typeof(int))).Unsafe.ShouldBe(false);
        }

        [Fact]
        public void RequiresReprojection()
        {
            new AddColumn("Entities", new Column("Col", typeof(int))).RequiresReprojectionOf.ShouldBe("Entities");
        }
    }
}