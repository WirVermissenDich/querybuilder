﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using Deviax.QueryBuilder.Visitors;

namespace Deviax.QueryBuilder
{
    public interface INameResolver
    {
        string DbToCSharp(string dbName);
        string CSharpToDb(string csharpName);
    }

    public static class AssignmentCache<T> where T : new()
    {
        public static Action<DbDataReader, T> Action;
    }

    public abstract class QueryExecutor
    {
        public static QueryExecutor DefaultExecutor;
        public static INameResolver NameResolver => DefaultExecutor.DefaultNameResolver;

        protected abstract INameResolver DefaultNameResolver { get; }

        private static readonly TypeConversionSpecialization DefaultTypeConversionSpeciliation = new DefaultTypeConversionSpecialization();
        private static IEnumerable<TypeConversionSpecialization> TypeConversionSpecializationsWithDefault
        {
            get
            {
                foreach (var tcs in TypeConversionSpecializations)
                    yield return tcs;

                yield return DefaultTypeConversionSpeciliation;
            }
        }

        private static readonly List<TypeConversionSpecialization> TypeConversionSpecializations = new List<TypeConversionSpecialization> {
            new ListTypeConversionSpecialization()
        };
        
        internal static void RegisterTypeConversionSpecialization(TypeConversionSpecialization tcs)
        {
            TypeConversionSpecializations.Add(tcs);
        }

        private void Process<TVisitor, TQuery>(TVisitor v, TQuery q, IVisitorResult r) where TVisitor : BaseVisitor, IQueryingVisitor<TQuery>
        {
            r.Start();
            v.Result = r;
            v.Process(q);
            r.Finished();
        }
        
        private DbCommand ToCommand<TVisitor, TQuery>(TQuery q, DbConnection con, DbTransaction tx = null) where TVisitor : BaseVisitor, IQueryingVisitor<TQuery>, new()
        {
            var r = new ActualCommandResult(con);
            Process(new TVisitor(), q, r);
            r.Command.Transaction = tx;
            return r.Command;
        }
        
        private string ToQueryText<TVisitor, TQuery>(TQuery q) where TVisitor : BaseVisitor, IQueryingVisitor<TQuery>, new()
        {
            var r = new ToSqlResult();
            Process(new TVisitor(), q, r);
            return r.StringBuilder + r.ParameterDescription;
        }
        
        public DbCommand ToCommand(BaseSelectQuery query, DbConnection con, DbTransaction tx = null) 
            => ToCommand<SelectVisitor, BaseSelectQuery>(query, con, tx);

        public PreparedCommand ToPreparedCommand(BaseSelectQuery query)
        {
            var r = new PreparingCommandResult();
            Process(new SelectVisitor(), query, r);
            return r.Result;
        }

        public DbCommand ToCommand(BaseUpdateQuery query, DbConnection con, DbTransaction tx = null)
            => ToCommand<UpdateVisitor, BaseUpdateQuery>(query, con, tx);
        
        public DbCommand ToCommand(BaseInsertQuery query, DbConnection con, DbTransaction tx = null)
            => ToCommand<InsertVisitor, BaseInsertQuery>(query, con, tx);

        public DbCommand ToCommand(BaseDeleteQuery query, DbConnection con, DbTransaction tx = null)
            => ToCommand<DeleteVisitor, BaseDeleteQuery>(query, con, tx);

        public string ToQueryText(BaseSelectQuery query)
            => ToQueryText<SelectVisitor, BaseSelectQuery>(query);

        public string ToQueryText(BaseDeleteQuery query)
            => ToQueryText<DeleteVisitor, BaseDeleteQuery>(query);

        public string ToQueryText(BaseInsertQuery query)
            => ToQueryText<InsertVisitor, BaseInsertQuery>(query);
        
        public string ToQueryText(BaseUpdateQuery query)
            => ToQueryText<UpdateVisitor, BaseUpdateQuery>(query);
        
        private static Action<DbDataReader, T> GenerateAssignment<T>(INameResolver nameResolver, DbDataReader reader) where T : new()
        {
            var expressions = new List<Expression>();

            var t = typeof(T);
            var targetParam = Expression.Parameter(t);
            var valueVariable = Expression.Variable(typeof(object));
            var readerParam = Expression.Parameter(typeof(DbDataReader));

            var getValueMethod = typeof(DbDataReader).GetTypeInfo().GetMethod("GetValue");

            for (var i = 0; i < reader.VisibleFieldCount; i++)
            {
                var dbName = reader.GetName(i);
                var csharpName = nameResolver.DbToCSharp(dbName);

                expressions.Add(Expression.Assign(valueVariable, Expression.Call(readerParam, getValueMethod, Expression.Constant(i))));

                Expression notNullBranch = null;

                if (csharpName == t.Name)
                {
                    csharpName += "F";
                }

                var field = t.GetTypeInfo().GetField(csharpName);

                if (field != null)
                {
                    var fi = field.FieldType.GetTypeInfo();
                    
                    foreach (var tcs in TypeConversionSpecializationsWithDefault)
                    {
                        if (tcs.Matches(fi, field.FieldType))
                        {
                            notNullBranch = Expression.Assign(
                                Expression.Field(targetParam, field),
                                tcs.Convert(fi, field.FieldType, valueVariable)
                            );
                            break;
                        }
                    }
                }
                else
                {

                    var property = t.GetTypeInfo().GetProperty(csharpName);

                    if (property == null)
                    {
                        throw new ArgumentException($"field or property {csharpName} on type {t} not found for database column {dbName}");
                    }

                    var ti = property.PropertyType.GetTypeInfo();
                    
                    foreach (var tcs in TypeConversionSpecializationsWithDefault)
                    {
                        if (tcs.Matches(ti, property.PropertyType))
                        {
                            notNullBranch = Expression.Assign(
                                Expression.Property(targetParam, property),
                                tcs.Convert(ti, property.PropertyType, valueVariable)
                            );
                            break;
                        }
                    }
                }

                var dbNullTest = Expression.Equal(valueVariable, Expression.Constant(DBNull.Value));

                expressions.Add(Expression.IfThen(Expression.IsFalse(dbNullTest), notNullBranch));
            }

            var block = Expression.Block(
                new[] {
                    valueVariable
                },
                expressions.ToArray()
            );

            var action = Expression.Lambda<Action<DbDataReader, T>>(block, readerParam, targetParam).Compile();
            return action;
        }

        public async Task<List<T>> ToList<T>(DbCommand cmd) where T : new()
        {
            var result = new List<T>();

            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                if (AssignmentCache<T>.Action == null)
                {
                    AssignmentCache<T>.Action = GenerateAssignment<T>(DefaultNameResolver, reader);
                }

                while (reader.Read())
                {
                    var item = new T();
                    AssignmentCache<T>.Action(reader, item);
                    result.Add(item);
                }
            }

            return result;
        }

        public async Task ForEach<T>(DbCommand cmd, Action<T> action) where T : new()
        {
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                if (AssignmentCache<T>.Action == null)
                {
                    AssignmentCache<T>.Action = GenerateAssignment<T>(DefaultNameResolver, reader);
                }

                while (reader.Read())
                {
                    var item = new T();
                    AssignmentCache<T>.Action(reader, item);
                    action(item);
                }
            }
        }

        public async Task ForEach<T>(DbCommand cmd, Func<T, Task> action) where T : new()
        {
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                if (AssignmentCache<T>.Action == null)
                {
                    AssignmentCache<T>.Action = GenerateAssignment<T>(DefaultNameResolver, reader);
                }

                while (reader.Read())
                {
                    var item = new T();
                    AssignmentCache<T>.Action(reader, item);
                    await action(item);
                }
            }
        }

        public async Task InsertBatched<T>(T[] items, int batchSize, DbConnection con, DbTransaction tx = null)
        {
            if (items.Length <= batchSize)
            {
                await Insert(items, con, tx);
            }
            else
            {
                foreach (var batch in items.Select((item, inx) => new {item, inx})
                    .GroupBy(x => x.inx / batchSize)
                    .Select(g => g.Select(x => x.item)))
                {
                    await Insert(batch.ToArray(), con, tx);
                }
            }
        }

        public async Task Insert<T>(T[] items, DbConnection con, DbTransaction tx = null)
        {
            var table = TypeToTableEntry<T>.DefaultTable;
            var q = new BaseInsertQuery(table).WithValues(items.Select((item, i) => TypeToTableEntry<T>.ToValues(item, table, i)).ToArray());
            var returningParts = TypeToTableEntry<T>.Returning(items[0], table);
            if (returningParts != null)
            {
                q = q.Returning(returningParts);
                var ids = await q.ScalarList<int>(con, tx);

                if (ids.Count != items.Length)
                    throw new InvalidOperationException();

                for (int i = 0; i < items.Length; i++)
                {
                    TypeToTableEntry<T>.ApplyReturning(items[i], ids[i]);
                }
            }
            else
            {
                await q.Execute(con, tx);
            }
        }

        public async Task<T> FirstOrDefault<T>(DbCommand cmd) where T : new()
        {
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                if (AssignmentCache<T>.Action == null)
                {
                    AssignmentCache<T>.Action = GenerateAssignment<T>(DefaultNameResolver, reader);
                }

                if (!reader.Read())
                    return default(T);

                var item = new T();
                AssignmentCache<T>.Action(reader, item);

                while (reader.Read()) { }
                return item;
            }
        }

        public async Task<List<T>> ToList<T>(BaseSelectQuery query, DbConnection con, DbTransaction tx = null) where T : new()
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ToList<T>(cmd).ConfigureAwait(false);
            }
        }

        public async Task<T> FirstOrDefault<T>(BaseSelectQuery query, DbConnection con, DbTransaction tx = null) where T : new()
        {
            using (var cmd = ToCommand(query, con, tx))
                return await FirstOrDefault<T>(cmd).ConfigureAwait(false);
        }


        public async Task<T> FirstOrDefault<T>(BaseUpdateQuery query, DbConnection con, DbTransaction tx = null) where T : new()
        {
            using (var cmd = ToCommand(query, con, tx))
                return await FirstOrDefault<T>(cmd).ConfigureAwait(false);
        }

        public async Task<T> ScalarResult<T>(BaseSelectQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarResult<T>(cmd);
            }
        }

        public async Task<T> ScalarResult<T>(BaseUpdateQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarResult<T>(cmd);
            }
        }

        public async Task<T> ScalarResult<T>(BaseInsertQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarResult<T>(cmd);
            }
        }

        public async Task<T> ScalarResult<T>(BaseDeleteQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarResult<T>(cmd);
            }
        }

        protected async Task<T> ScalarResult<T>(DbCommand cmd)
        {
            var val = await cmd.ExecuteScalarAsync().ConfigureAwait(false);

            if (val == DBNull.Value)
                return default(T);

            if (val is T)
                return (T) val;

            if (typeof(T) == typeof(long))
                return (T) (object) Convert.ToInt64(val);

            if (typeof(T) == typeof(int))
                return (T) (object) Convert.ToInt32(val);

            if (typeof(T) == typeof(short))
                return (T) (object) Convert.ToInt16(val);
            
            throw new ArgumentException();
        }

        public async Task<List<T>> ScalarListResult<T>(BaseSelectQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarListResult<T>(cmd);
            }
        }

        public async Task<List<T>> ScalarListResult<T>(BaseUpdateQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarListResult<T>(cmd);
            }
        }

        public async Task<List<T>> ScalarListResult<T>(BaseInsertQuery query, DbConnection con, DbTransaction tx)
        {
            using (var cmd = ToCommand(query, con, tx))
            {
                return await ScalarListResult<T>(cmd);
            }
        }

        protected async Task<List<T>>  ScalarListResult<T>(DbCommand cmd)
        {
            var result = new List<T>();

            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (reader.Read())
                {
                    var val = reader[0];

                    if (val == DBNull.Value)
                        result.Add(default(T));
                    else
                    {
                        if (val is T)
                        {
                            result.Add((T) val);
                        }
                        else if (typeof(T) == typeof(long))
                        {
                            result.Add((T) (object) Convert.ToInt64(val));
                        }
                        else if (typeof(T) == typeof(int))
                        {
                            result.Add((T) (object) Convert.ToInt32(val));
                        }
                        else if (typeof(T) == typeof(short))
                        {
                            result.Add((T) (object) Convert.ToInt16(val));
                        }
                        else
                        {
                            throw new ArgumentException();
                        }
                    }
                }
            }

            return result;
        }
    }
}