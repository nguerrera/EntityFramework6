// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

namespace System.Data.Entity.Core.Objects.ELinq
{
    using System.Collections.Generic;
    using System.Data.Entity.Core.Common;
    using System.Data.Entity.Core.Common.CommandTrees;
    using System.Data.Entity.Core.Common.CommandTrees.ExpressionBuilder;
    using System.Data.Entity.Core.Common.EntitySql;
    using System.Data.Entity.Core.Common.Utils;
    using System.Data.Entity.Core.Metadata.Edm;
    using System.Data.Entity.Core.Metadata.Edm.Provider;
    using System.Data.Entity.Resources;
    using System.Data.Entity.Utilities;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Text;

    // <summary>
    // Class supporting conversion of LINQ expressions to EDM CQT expressions.
    // </summary>
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling")]
    internal sealed partial class ExpressionConverter
    {
        #region Fields

        private readonly Funcletizer _funcletizer;
        private readonly Perspective _perspective;
        private readonly Expression _expression;
        private readonly BindingContext _bindingContext;
        private Func<bool> _recompileRequired;
        private List<Tuple<ObjectParameter, QueryParameterExpression>> _parameters;
        private Dictionary<DbExpression, Span> _spanMappings;
        private MergeOption? _mergeOption;
        private Dictionary<Type, InitializerMetadata> _initializers;
        private Span _span;
        private HashSet<ObjectQuery> _inlineEntitySqlQueries;
        private int _ignoreInclude;
        private readonly AliasGenerator _aliasGenerator = new AliasGenerator("LQ", 0);
        private readonly OrderByLifter _orderByLifter;

        #region Consts

        private const string s_visualBasicAssemblyFullName =
            "Microsoft.VisualBasic, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";

        private static readonly Dictionary<ExpressionType, Translator> _translators = InitializeTranslators();

        // <summary>
        // Gets the name of the key column appearing in ELinq GroupBy projections
        // </summary>
        internal const string KeyColumnName = "Key";

        // <summary>
        // Gets the name of the group column appearing in ELinq CQTs (used in GroupBy expressions)
        // </summary>
        internal const string GroupColumnName = "Group";

        // <summary>
        // Gets the name of the parent column appearing in ELinq EntityCollection projections
        // </summary>
        internal const string EntityCollectionOwnerColumnName = "Owner";

        // <summary>
        // Gets the name of the children column appearing in ELinq EntityCollection projections
        // </summary>
        internal const string EntityCollectionElementsColumnName = "Elements";

        // <summary>
        // The Edm namespace name, used for canonical functions
        // </summary>
        internal const string EdmNamespaceName = "Edm";

        #endregion

        #region Canonical Function Names

        private const string Concat = "Concat";
        private const string IndexOf = "IndexOf";
        private const string Length = "Length";
        private const string Right = "Right";
        private const string Substring = "Substring";
        private const string ToUpper = "ToUpper";
        private const string ToLower = "ToLower";
        private const string Trim = "Trim";
        private const string LTrim = "LTrim";
        private const string RTrim = "RTrim";
        private const string Reverse = "Reverse";
        private const string BitwiseAnd = "BitwiseAnd";
        private const string BitwiseOr = "BitwiseOr";
        private const string BitwiseNot = "BitwiseNot";
        private const string BitwiseXor = "BitwiseXor";
        private const string CurrentUtcDateTime = "CurrentUtcDateTime";
        private const string CurrentDateTimeOffset = "CurrentDateTimeOffset";
        private const string CurrentDateTime = "CurrentDateTime";
        private const string Year = "Year";
        private const string Month = "Month";
        private const string Day = "Day";
        private const string Hour = "Hour";
        private const string Minute = "Minute";
        private const string Second = "Second";
        private const string Millisecond = "Millisecond";

        #endregion

        #region Additional Entity function names

        private const string Like = "Like";
        private const string AsUnicode = "AsUnicode";
        private const string AsNonUnicode = "AsNonUnicode";

        #endregion

        #endregion

        #region Constructors and static initializors

        internal ExpressionConverter(Funcletizer funcletizer, Expression expression)
        {
            DebugCheck.NotNull(funcletizer);
            DebugCheck.NotNull(expression);

            // Funcletize the expression (identify subexpressions that should be evaluated
            // locally)
            _funcletizer = funcletizer;
            expression = funcletizer.Funcletize(expression, out _recompileRequired);

            // Normalize the expression (replace obfuscated parts of the tree with simpler nodes)
            var normalizer = new LinqExpressionNormalizer();
            _expression = normalizer.Visit(expression);

            _perspective = funcletizer.RootContext.Perspective;
            _bindingContext = new BindingContext();
            _ignoreInclude = 0;
            _orderByLifter = new OrderByLifter(_aliasGenerator);
        }

        // initialize translator dictionary (which support identification of translators
        // for LINQ expression node types)
        private static Dictionary<ExpressionType, Translator> InitializeTranslators()
        {
            var translators = new Dictionary<ExpressionType, Translator>();
            foreach (var translator in GetTranslators())
            {
                foreach (var nodeType in translator.NodeTypes)
                {
                    translators.Add(nodeType, translator);
                }
            }

            return translators;
        }

        private static IEnumerable<Translator> GetTranslators()
        {
            yield return new AndAlsoTranslator();
            yield return new OrElseTranslator();
            yield return new LessThanTranslator();
            yield return new LessThanOrEqualsTranslator();
            yield return new GreaterThanTranslator();
            yield return new GreaterThanOrEqualsTranslator();
            yield return new EqualsTranslator();
            yield return new NotEqualsTranslator();
            yield return new ConvertTranslator();
            yield return new ConstantTranslator();
            yield return new NotTranslator();
            yield return new MemberAccessTranslator();
            yield return new ParameterTranslator();
            yield return new MemberInitTranslator();
            yield return new NewTranslator();
            yield return new AddTranslator();
            yield return new ConditionalTranslator();
            yield return new DivideTranslator();
            yield return new ModuloTranslator();
            yield return new SubtractTranslator();
            yield return new MultiplyTranslator();
            yield return new PowerTranslator();
            yield return new NegateTranslator();
            yield return new UnaryPlusTranslator();
            yield return new MethodCallTranslator();
            yield return new CoalesceTranslator();
            yield return new AsTranslator();
            yield return new IsTranslator();
            yield return new QuoteTranslator();
            yield return new AndTranslator();
            yield return new OrTranslator();
            yield return new ExclusiveOrTranslator();
            yield return new ExtensionTranslator();
            yield return new NewArrayInitTranslator();
            yield return new ListInitTranslator();
            yield return new NotSupportedTranslator(
                ExpressionType.LeftShift,
                ExpressionType.RightShift,
                ExpressionType.ArrayLength,
                ExpressionType.ArrayIndex,
                ExpressionType.Invoke,
                ExpressionType.Lambda,
                ExpressionType.NewArrayBounds);
        }

        #endregion

        #region Properties

        private EdmItemCollection EdmItemCollection
        {
            get { return (EdmItemCollection)_funcletizer.RootContext.MetadataWorkspace.GetItemCollection(DataSpace.CSpace, true); }
        }

        internal DbProviderManifest ProviderManifest
        {
            get
            {
                return
                    ((StoreItemCollection)_funcletizer.RootContext.MetadataWorkspace.GetItemCollection(DataSpace.SSpace)).
                        ProviderManifest;
            }
        }

        internal IEnumerable<Tuple<ObjectParameter, QueryParameterExpression>> GetParameters()
        {
            if (null != _parameters)
            {
                return _parameters;
            }
            return null;
        }

        internal MergeOption? PropagatedMergeOption
        {
            get { return _mergeOption; }
        }

        internal Span PropagatedSpan
        {
            get { return _span; }
        }

        internal Func<bool> RecompileRequired
        {
            get { return _recompileRequired; }
        }

        internal int IgnoreInclude
        {
            get { return _ignoreInclude; }
            set { _ignoreInclude = value; }
        }

        internal AliasGenerator AliasGenerator
        {
            get { return _aliasGenerator; }
        }

        #endregion

        #region Internal methods

        // Convert the LINQ expression to a CQT expression and (optional) Span information.
        // Span information will only be present if ObjectQuery instances that specify Spans
        // are referenced from the LINQ expression in a manner consistent with the Span combination
        // rules, otherwise the Span for the CQT expression will be null.
        internal DbExpression Convert()
        {
            var result = TranslateExpression(_expression);
            if (!TryGetSpan(result, out _span))
            {
                _span = null;
            }
            return result;
        }

        internal static bool CanFuncletizePropertyInfo(PropertyInfo propertyInfo)
        {
            return MemberAccessTranslator.CanFuncletizePropertyInfo(propertyInfo);
        }

        internal bool CanIncludeSpanInfo()
        {
            return (_ignoreInclude == 0);
        }

        #endregion

        #region Private Methods

        private void NotifyMergeOption(MergeOption mergeOption)
        {
            if (!_mergeOption.HasValue)
            {
                _mergeOption = mergeOption;
            }
        }

        // Requires: metadata must not be null.
        //
        // Effects: adds initializer metadata to this query context.
        // 
        // Ensures that the given initializer metadata is valid within the current converter context.
        // We do not allow two incompatible structures representing the same type within a query, e.g.,
        //
        //      outer.Join(inner, o => new Xyz { X = o.ID }, i => new Xyz { Y = i.ID }, ...
        //
        // since this introduces a discrepancy between the CLR (where comparisons between Xyz are aware
        // of both X and Y) and in ELinq (where comparisons are based on the row structure only), resulting
        // in the following join predicates:
        //
        //      Linq: xyz1 == xyz2 (which presumably amounts to xyz1.X == xyz2.X && xyz1.Y == xyz2.Y
        //      ELinq: xyz1.X == xyz2.Y
        //
        // Similar problems occur with set operations such as Union and Concat, where one of the initialization
        // patterns may be ignored.
        //
        // This method performs an overly strict check, requiring that all initializers for a given type
        // are structurally equivalent.
        [SuppressMessage("Microsoft.Usage", "CA2301", Justification = "metadata.ClrType is not expected to be an Embedded Interop Type.")]
        internal void ValidateInitializerMetadata(InitializerMetadata metadata)
        {
            DebugCheck.NotNull(metadata);
            InitializerMetadata existingMetadata;
            if (_initializers != null
                && _initializers.TryGetValue(metadata.ClrType, out existingMetadata))
            {
                // Verify the initializers are compatible.
                if (!metadata.Equals(existingMetadata))
                {
                    throw new NotSupportedException(
                        Strings.ELinq_UnsupportedHeterogeneousInitializers(
                            DescribeClrType(metadata.ClrType)));
                }
            }
            else
            {
                // Register the metadata so that subsequent initializers for this type can be verified.
                if (_initializers == null)
                {
                    _initializers = new Dictionary<Type, InitializerMetadata>();
                }
                _initializers.Add(metadata.ClrType, metadata);
            }
        }

        private void AddParameter(QueryParameterExpression queryParameter)
        {
            if (null == _parameters)
            {
                _parameters = new List<Tuple<ObjectParameter, QueryParameterExpression>>();
            }
            if (!_parameters.Select(p => p.Item2).Contains(queryParameter))
            {
                var parameter = new ObjectParameter(queryParameter.ParameterReference.ParameterName, queryParameter.Type);
                _parameters.Add(new Tuple<ObjectParameter, QueryParameterExpression>(parameter, queryParameter));
            }
        }

        private bool IsQueryRoot(Expression Expression)
        {
            //
            // An expression is the query root if it was the expression used
            // when constructing this converter.
            //
            return ReferenceEquals(_expression, Expression);
        }

        #region Span Mapping maintenance methods

        // <summary>
        // Adds a new mapping from DbExpression => Span information for the specified expression,
        // after first ensuring that the mapping dictionary has been instantiated.
        // </summary>
        // <param name="expression"> The expression for which Span information should be added </param>
        // <param name="span">
        // The Span information, which may be <c>null</c> . If <c>null</c> , no attempt is made to update the dictionary of span mappings.
        // </param>
        // <returns>
        // The original <paramref name="expression" /> argument, to allow <c>return AddSpanMapping(expression, span)</c> scenarios
        // </returns>
        private DbExpression AddSpanMapping(DbExpression expression, Span span)
        {
            if (span != null
                && CanIncludeSpanInfo())
            {
                if (null == _spanMappings)
                {
                    _spanMappings = new Dictionary<DbExpression, Span>();
                }
                Span storedSpan = null;
                if (_spanMappings.TryGetValue(expression, out storedSpan))
                {
                    foreach (var sp in span.SpanList)
                    {
                        storedSpan.AddSpanPath(sp);
                    }
                    _spanMappings[expression] = storedSpan;
                }
                else
                {
                    _spanMappings[expression] = span;
                }
            }

            return expression;
        }

        // <summary>
        // Attempts to retrieve Span information for the specified DbExpression.
        // </summary>
        // <param name="expression"> The expression for which Span information should be retrieved. </param>
        // <param name="span"> Will contain the Span information for the specified expression if it is present in the Span mapping dictionary. </param>
        // <returns>
        // <c>true</c> if Span information was retrieved for the specified expression and <paramref name="span" /> now contains this information; otherwise <c>false</c> .
        // </returns>
        private bool TryGetSpan(DbExpression expression, out Span span)
        {
            if (_spanMappings != null)
            {
                return _spanMappings.TryGetValue(expression, out span);
            }

            span = null;
            return false;
        }

        // <summary>
        // Removes the Span mapping entry for the specified <paramref name="from" /> expression,
        // and creates a new entry for the specified <paramref name="to" /> expression that maps
        // to the <paramref name="from" /> expression's original Span information. If no Span
        // information is present for the specified <paramref name="from" /> expression then no
        // changes are made to the Span mapping dictionary.
        // </summary>
        // <param name="from"> The expression from which to take Span information </param>
        // <param name="to"> The expression to which the Span information should be applied </param>
        private void ApplySpanMapping(DbExpression from, DbExpression to)
        {
            Span argumentSpan;
            if (TryGetSpan(from, out argumentSpan))
            {
                AddSpanMapping(to, argumentSpan);
            }
        }

        // <summary>
        // Unifies the Span information from the specified <paramref name="left" /> and <paramref name="right" />
        // expressions, and applies it to the specified <paramref name="to" /> expression. Unification proceeds
        // as follows:
        // - If neither <paramref name="left" /> nor <paramref name="right" /> have Span information, no changes are made
        // - If one of <paramref name="left" /> or <paramref name="right" /> has Span information, that single Span information
        // entry is removed from the Span mapping dictionary and used to create a new entry that maps from the
        // <paramref
        //     name="to" />
        // expression to the Span information.
        // - If both <paramref name="left" /> and <paramref name="right" /> have Span information, both entries are removed
        // from the Span mapping dictionary, a new Span is created that contains the union of the original Spans, and
        // a new entry is added to the dictionary that maps from <paramref name="to" /> expression to this new Span.
        // </summary>
        // <param name="left"> The first expression argument </param>
        // <param name="right"> The second expression argument </param>
        // <param name="to"> The result expression </param>
        private void UnifySpanMappings(DbExpression left, DbExpression right, DbExpression to)
        {
            Span leftSpan = null;
            Span rightSpan = null;

            var hasLeftSpan = TryGetSpan(left, out leftSpan);
            var hasRightSpan = TryGetSpan(right, out rightSpan);
            if (!hasLeftSpan
                && !hasRightSpan)
            {
                return;
            }

            Debug.Assert(leftSpan != null || rightSpan != null, "Span mappings contain null?");
            AddSpanMapping(to, Span.CopyUnion(leftSpan, rightSpan));
        }

        #endregion

        // The following methods correspond to query builder methods on ObjectQuery
        // and MUST be called by expression translators (instead of calling the equivalent
        // CommandTree.CreateXxExpression methods) to ensure that Span information flows
        // correctly to the root of the Command Tree as it is constructed by converting
        // the LINQ expression tree. Each method correctly maintains a Span mapping (if required)
        // for its resulting expression, based on the Span mappings of its argument expression(s).

        private DbDistinctExpression Distinct(DbExpression argument)
        {
            var retExpr = argument.Distinct();
            ApplySpanMapping(argument, retExpr);
            return retExpr;
        }

        private DbExceptExpression Except(DbExpression left, DbExpression right)
        {
            var retExpr = left.Except(right);
            ApplySpanMapping(left, retExpr);
            return retExpr;
        }

        private DbExpression Filter(DbExpressionBinding input, DbExpression predicate)
        {
            var retExpr = _orderByLifter.Filter(input, predicate);
            ApplySpanMapping(input.Expression, retExpr);
            return retExpr;
        }

        private DbIntersectExpression Intersect(DbExpression left, DbExpression right)
        {
            var retExpr = left.Intersect(right);
            UnifySpanMappings(left, right, retExpr);
            return retExpr;
        }

        private DbExpression Limit(DbExpression argument, DbExpression limit)
        {
            var retExpr = _orderByLifter.Limit(argument, limit);
            ApplySpanMapping(argument, retExpr);
            return retExpr;
        }

        private DbExpression OfType(DbExpression argument, TypeUsage ofType)
        {
            var retExpr = _orderByLifter.OfType(argument, ofType);
            ApplySpanMapping(argument, retExpr);
            return retExpr;
        }

        private DbExpression Project(DbExpressionBinding input, DbExpression projection)
        {
            var retExpr = _orderByLifter.Project(input, projection);
            // For identity projection only, the Span is preserved
            if (projection.ExpressionKind == DbExpressionKind.VariableReference
                &&
                ((DbVariableReferenceExpression)projection).VariableName.Equals(input.VariableName, StringComparison.Ordinal))
            {
                ApplySpanMapping(input.Expression, retExpr);
            }
            return retExpr;
        }

        private DbSortExpression Sort(DbExpressionBinding input, IList<DbSortClause> keys)
        {
            var retExpr = input.Sort(keys);
            ApplySpanMapping(input.Expression, retExpr);
            return retExpr;
        }

        private DbExpression Skip(DbExpressionBinding input, DbExpression skipCount)
        {
            var retExpr = _orderByLifter.Skip(input, skipCount);
            ApplySpanMapping(input.Expression, retExpr);
            return retExpr;
        }

        private DbUnionAllExpression UnionAll(DbExpression left, DbExpression right)
        {
            var retExpr = left.UnionAll(right);
            UnifySpanMappings(left, right, retExpr);
            return retExpr;
        }

        // <summary>
        // Gets the target type for a CQT cast operation.
        // </summary>
        // <returns> Appropriate type usage, or null if this is a "no-op" </returns>
        private TypeUsage GetCastTargetType(TypeUsage fromType, Type toClrType, Type fromClrType, bool preserveCastForDateTime)
        {
            // An IQueryable can report its type as ObjectQuery, IQueryable, or IOrderedQueryable depending on how the type and
            // expression tree were created. At this point in the translation, unwrapping of the DbQuery to ObjectQuery has already
            // happened and checking for something other than ObjectQuery has already been done. Therefore, from a CQT translation
            // perspective we can treat all these types as the same and this therefore becomes a no-op.
            if (fromClrType != null
                && fromClrType.IsGenericType()
                && toClrType.IsGenericType()
                && (fromClrType.GetGenericTypeDefinition() == typeof(ObjectQuery<>)
                    || fromClrType.GetGenericTypeDefinition() == typeof(IQueryable<>)
                    || fromClrType.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>))
                && (toClrType.GetGenericTypeDefinition() == typeof(ObjectQuery<>)
                    || toClrType.GetGenericTypeDefinition() == typeof(IQueryable<>)
                    || toClrType.GetGenericTypeDefinition() == typeof(IOrderedQueryable<>))
                && fromClrType.GetGenericArguments()[0] == toClrType.GetGenericArguments()[0])
            {
                return null;
            }

            //ignore System.Enum
            if (fromClrType != null
                && TypeSystem.GetNonNullableType(fromClrType).IsEnum
                && toClrType == typeof(Enum))
            {
                return null;
            }

            // If the types are the same or the fromType is assignable to toType, return null
            // (indicating no cast is required)
            TypeUsage toType;
            if (TryGetValueLayerType(toClrType, out toType)
                && CanOmitCast(fromType, toType, preserveCastForDateTime))
            {
                return null;
            }

            // Check that the cast is supported and adjust the target type as necessary.
            toType = ValidateAndAdjustCastTypes(toType, fromType, toClrType, fromClrType);

            return toType;
        }

        // <summary>
        // Check that the given cast specification is supported and if necessary adjust target type (for instance
        // add precision and scale for Integral -> Decimal casts)
        // </summary>
        private static TypeUsage ValidateAndAdjustCastTypes(TypeUsage toType, TypeUsage fromType, Type toClrType, Type fromClrType)
        {
            // only support primitives if real casting is involved
            if (toType == null
                || !TypeSemantics.IsScalarType(toType)
                || !TypeSemantics.IsScalarType(fromType))
            {
                throw new NotSupportedException(Strings.ELinq_UnsupportedCast(DescribeClrType(fromClrType), DescribeClrType(toClrType)));
            }

            var fromTypeKind = Helper.AsPrimitive(fromType.EdmType).PrimitiveTypeKind;
            var toTypeKind = Helper.AsPrimitive(toType.EdmType).PrimitiveTypeKind;

            if (toTypeKind == PrimitiveTypeKind.Decimal)
            {
                // Can't figure out the right precision and scale for decimal, so only accept integer types
                switch (fromTypeKind)
                {
                    case PrimitiveTypeKind.Byte:
                    case PrimitiveTypeKind.Int16:
                    case PrimitiveTypeKind.Int32:
                    case PrimitiveTypeKind.Int64:
                    case PrimitiveTypeKind.SByte:
                        // adjust precision and scale to ensure sufficient width
                        toType = TypeUsage.CreateDecimalTypeUsage((PrimitiveType)toType.EdmType, 19, 0);
                        break;
                    default:
                        throw new NotSupportedException(Strings.ELinq_UnsupportedCastToDecimal);
                }
            }

            return toType;
        }

        // <summary>
        // Determines if an instance of fromType can be assigned to an instance of toType using
        // CLR semantics. in case of primitive type, it must rely on identity since unboxing primitive requires
        // exact match. for nominal types, rely on subtyping.
        // </summary>
        private static bool CanOmitCast(TypeUsage fromType, TypeUsage toType, bool preserveCastForDateTime)
        {
            var isPrimitiveType = TypeSemantics.IsPrimitiveType(fromType);

            //SQLBUDT #573573: This is to allow for a workaround on Katmai via explicit casting by the user.
            // The issue is that SqlServer's type Date maps to Edm.DateTime, same as SqlServer's DateTime and SmallDateTime.
            // However the conversion is not possible for all values of Date.

            //Note: we could also call here TypeSemantics.IsPrimitiveType(TypeUsage type, PrimitiveTypeKind primitiveTypeKind),
            //  but that checks again whether the type is primitive
            if (isPrimitiveType
                && preserveCastForDateTime
                && ((PrimitiveType)fromType.EdmType).PrimitiveTypeKind == PrimitiveTypeKind.DateTime)
            {
                return false;
            }

            if (TypeUsageEquals(fromType, toType))
            {
                return true;
            }

            if (isPrimitiveType)
            {
                return fromType.EdmType.EdmEquals(toType.EdmType);
            }

            return TypeSemantics.IsSubTypeOf(fromType, toType);
        }

        // <summary>
        // Gets the target type for an Is or As expression.
        // </summary>
        // <param name="operationType"> Type of operation; used in error reporting. </param>
        // <param name="toClrType"> Test or return type. </param>
        // <param name="fromClrType"> Input type in CLR metadata. </param>
        // <returns> Appropriate target type usage. </returns>
        private TypeUsage GetIsOrAsTargetType(ExpressionType operationType, Type toClrType, Type fromClrType)
        {
            Debug.Assert(operationType == ExpressionType.TypeAs || operationType == ExpressionType.TypeIs);

            // Interpret all type information
            TypeUsage toType;
            if (!TryGetValueLayerType(toClrType, out toType)
                ||
                (!TypeSemantics.IsEntityType(toType) &&
                 !TypeSemantics.IsComplexType(toType)))
            {
                throw new NotSupportedException(
                    Strings.ELinq_UnsupportedIsOrAs(
                        operationType,
                        DescribeClrType(fromClrType), DescribeClrType(toClrType)));
            }

            return toType;
        }

        // requires: inlineQuery is not null and inlineQuery is Entity-SQL query
        // effects: interprets the given query as an inline query in the current expression and unites
        // the current query context with the context for the inline query. If the given query specifies
        // span information, then an entry is added to the span mapping dictionary from the CQT expression
        // that is the root of the inline query, to the span information that was present in the inline
        // query's Span property.
        private DbExpression TranslateInlineQueryOfT(ObjectQuery inlineQuery)
        {
            if (!ReferenceEquals(_funcletizer.RootContext, inlineQuery.QueryState.ObjectContext))
            {
                throw new NotSupportedException(Strings.ELinq_UnsupportedDifferentContexts);
            }

            // Check if the inline query has been encountered so far. If so, we don't need to
            // include its parameters again. We do however need to translate it to a new
            // DbExpression instance since the expressions may be tagged with span information
            // and we don't want to mistakenly apply the directive to the wrong part of the query.
            if (null == _inlineEntitySqlQueries)
            {
                _inlineEntitySqlQueries = new HashSet<ObjectQuery>();
            }
            var isNewInlineQuery = _inlineEntitySqlQueries.Add(inlineQuery);

            // The ObjectQuery should be Entity-SQL-based at this point. All other query types are currently
            // inlined.
            var esqlState = (EntitySqlQueryState)inlineQuery.QueryState;

            // We will produce the translated expression by parsing the Entity-SQL query text.
            DbExpression resultExpression = null;

            // If we are not converting a compiled query, or the referenced Entity-SQL ObjectQuery
            // does not have parameters (and so no parameter references can be in the parsed tree)
            // then the Entity-SQL can be parsed directly using the conversion command tree.
            var objectParameters = inlineQuery.QueryState.Parameters;
            if (!_funcletizer.IsCompiledQuery
                || objectParameters == null
                || objectParameters.Count == 0)
            {
                // Add parameters if they exist and we haven't yet encountered this inline query.
                if (isNewInlineQuery && objectParameters != null)
                {
                    // Copy the parameters into the aggregated parameter collection - this will result
                    // in an exception if any duplicate parameter names are encountered.
                    if (_parameters == null)
                    {
                        _parameters = new List<Tuple<ObjectParameter, QueryParameterExpression>>();
                    }
                    foreach (var prm in inlineQuery.QueryState.Parameters)
                    {
                        _parameters.Add(new Tuple<ObjectParameter, QueryParameterExpression>(prm.ShallowCopy(), null));
                    }
                }

                resultExpression = esqlState.Parse();
            }
            else
            {
                // We are converting a compiled query and parameters are present on the referenced ObjectQuery.
                // The set of parameters available to a compiled query is fixed (so that adding/removing parameters
                // to/from a referenced ObjectQuery does not invalidate the compiled query's execution plan), so the
                // referenced ObjectQuery will be fully inlined by replacing each parameter reference with a
                // DbConstantExpression containing the value of the referenced parameter.
                resultExpression = esqlState.Parse();
                resultExpression = ParameterReferenceRemover.RemoveParameterReferences(resultExpression, objectParameters);
            }

            return resultExpression;
        }

        private class ParameterReferenceRemover : DefaultExpressionVisitor
        {
            internal static DbExpression RemoveParameterReferences(DbExpression expression, ObjectParameterCollection availableParameters)
            {
                var remover = new ParameterReferenceRemover(availableParameters);
                return remover.VisitExpression(expression);
            }

            private readonly ObjectParameterCollection objectParameters;

            private ParameterReferenceRemover(ObjectParameterCollection availableParams)
            {
                DebugCheck.NotNull(availableParams);

                objectParameters = availableParams;
            }

            public override DbExpression Visit(DbParameterReferenceExpression expression)
            {
                Check.NotNull(expression, "expression");

                if (objectParameters.Contains(expression.ParameterName))
                {
                    // A DbNullExpression is required for null values; DbConstantExpression otherwise.
                    var objParam = objectParameters[expression.ParameterName];
                    if (null == objParam.Value)
                    {
                        return expression.ResultType.Null();
                    }
                    else
                    {
                        // This will throw if the value is incompatible with the result type.
                        return expression.ResultType.Constant(objParam.Value);
                    }
                }
                return expression;
            }
        }

        // creates a CQT cast expression given the source and target CLR type
        private DbExpression CreateCastExpression(DbExpression source, Type toClrType, Type fromClrType)
        {
            // see if the source can be normalized as a set
            var setSource = NormalizeSetSource(source);
            if (!ReferenceEquals(source, setSource))
            {
                // if the resulting cast is a no-op (no either kind is supported
                // for set sources), yield the source
                if (null == GetCastTargetType(setSource.ResultType, toClrType, fromClrType, true))
                {
                    return source;
                }
            }

            // try to find the appropriate target for the cast
            var toType = GetCastTargetType(source.ResultType, toClrType, fromClrType, true);
            if (null == toType)
            {
                // null indicates a no-op cast (from the perspective of the model)
                return source;
            }

            return source.CastTo(toType);
        }

        // Utility translator method for lambda expressions. Given a lambda expression and its translated
        // inputs, translates the lambda expression, assuming the input is a collection
        private DbExpression TranslateLambda(LambdaExpression lambda, DbExpression input, out DbExpressionBinding binding)
        {
            input = NormalizeSetSource(input);

            // create binding context for this lambda expression
            binding = input.BindAs(_aliasGenerator.Next());

            return TranslateLambda(lambda, binding.Variable);
        }

        // Utility translator method for lambda expressions. Given a lambda expression and its translated
        // inputs, translates the lambda expression, assuming the input is a collection
        private DbExpression TranslateLambda(
            LambdaExpression lambda, DbExpression input, string bindingName, out DbExpressionBinding binding)
        {
            input = NormalizeSetSource(input);

            // create binding context for this lambda expression
            binding = input.BindAs(bindingName);

            return TranslateLambda(lambda, binding.Variable);
        }

        // Utility translator method for lambda expressions that are part of group by. Given a lambda expression and its translated
        // inputs, translates the lambda expression, assuming the input needs to be used as a grouping input
        private DbExpression TranslateLambda(LambdaExpression lambda, DbExpression input, out DbGroupExpressionBinding binding)
        {
            input = NormalizeSetSource(input);

            // create binding context for this lambda expression
            var alias = _aliasGenerator.Next();
            binding = input.GroupBindAs(alias, string.Format(CultureInfo.InvariantCulture, "Group{0}", alias));

            return TranslateLambda(lambda, binding.Variable);
        }

        // Utility translator method for lambda expressions. Given a lambda expression and its translated
        // inputs, translates the lambda expression
        private DbExpression TranslateLambda(LambdaExpression lambda, DbExpression input)
        {
            var scopeBinding = new Binding(lambda.Parameters[0], input);

            // push the binding scope
            _bindingContext.PushBindingScope(scopeBinding);

            // translate expression within this binding scope
#if DEBUG
            var preValue = _ignoreInclude;
#endif
            _ignoreInclude++;
            var result = TranslateExpression(lambda.Body);
            _ignoreInclude--;
#if DEBUG
            Debug.Assert(preValue == _ignoreInclude);
#endif

            // pop binding scope
            _bindingContext.PopBindingScope();

            return result;
        }

        // effects: unwraps any "structured" set sources such as IGrouping instances
        // (which acts as both a set and a structure containing a property)
        private DbExpression NormalizeSetSource(DbExpression input)
        {
            DebugCheck.NotNull(input);

            // If input looks like "select x from (...) as x", rewrite it as "(...)".
            // If input has span information attached to it then leave it as is, otherwise 
            // span info will be lost.
            Span span;
            if (input.ExpressionKind == DbExpressionKind.Project
                && !TryGetSpan(input, out span))
            {
                var project = (DbProjectExpression)input;
                if (project.Projection
                    == project.Input.Variable)
                {
                    input = project.Input.Expression;
                }
            }

            // determine if the lambda input is an IGrouping or EntityCollection that needs to be unwrapped
            InitializerMetadata initializerMetadata;
            if (InitializerMetadata.TryGetInitializerMetadata(input.ResultType, out initializerMetadata))
            {
                if (initializerMetadata.Kind
                    == InitializerMetadataKind.Grouping)
                {
                    // for group by, redirect the binding to the group (rather than the property)
                    input = input.Property(GroupColumnName);
                }
                else if (initializerMetadata.Kind
                         == InitializerMetadataKind.EntityCollection)
                {
                    // for entity collection, redirect the binding to the children
                    input = input.Property(EntityCollectionElementsColumnName);
                }
            }
            return input;
        }

        // Given a method call expression, returns the given lambda argument (unwrapping quote or closure references where 
        // necessary)
        private LambdaExpression GetLambdaExpression(MethodCallExpression callExpression, int argumentOrdinal)
        {
            var argument = callExpression.Arguments[argumentOrdinal];
            return (LambdaExpression)GetLambdaExpression(argument);
        }

        private Expression GetLambdaExpression(Expression argument)
        {
            if (ExpressionType.Lambda
                == argument.NodeType)
            {
                return argument;
            }
            else if (ExpressionType.Quote
                     == argument.NodeType)
            {
                return GetLambdaExpression(((UnaryExpression)argument).Operand);
            }
            else if (ExpressionType.Call 
                     == argument.NodeType)
            {
                if (typeof(Expression).IsAssignableFrom(argument.Type))
                {
                    var expressionMethod = Expression.Lambda<Func<Expression>>(argument).Compile();

                    return GetLambdaExpression(
                        expressionMethod.Invoke());
                }
            } 
            else if (ExpressionType.Invoke
                     == argument.NodeType)
            {
                if (typeof(Expression).IsAssignableFrom(argument.Type))
                {
                    var expressionMethod = Expression.Lambda<Func<Expression>>(argument).Compile();

                    return GetLambdaExpression(
                        expressionMethod.Invoke());
                }
            }

            throw new InvalidOperationException(
                Strings.ADP_InternalProviderError((int)EntityUtil.InternalErrorCode.UnexpectedLinqLambdaExpressionFormat));
        }

        // Translate a LINQ expression acting as a set input to a CQT expression
        private DbExpression TranslateSet(Expression linq)
        {
            return NormalizeSetSource(TranslateExpression(linq));
        }

        // Translate a LINQ expression to a CQT expression.
        private DbExpression TranslateExpression(Expression linq)
        {
            DebugCheck.NotNull(linq);

            DbExpression result;
            if (!_bindingContext.TryGetBoundExpression(linq, out result))
            {
                // translate to a CQT expression
                Translator translator;
                if (_translators.TryGetValue(linq.NodeType, out translator))
                {
                    result = translator.Translate(this, linq);
                }
                else
                {
                    throw EntityUtil.InternalError(
                        EntityUtil.InternalErrorCode.UnknownLinqNodeType, -1,
                        linq.NodeType.ToString());
                }
            }
            return result;
        }

        // Cast expression to align types between CQT and eLINQ
        private DbExpression AlignTypes(DbExpression cqt, Type toClrType)
        {
            Type fromClrType = null; // not used in this code path
            var toType = GetCastTargetType(cqt.ResultType, toClrType, fromClrType, false);
            if (null != toType)
            {
                return cqt.CastTo(toType);
            }
            else
            {
                return cqt;
            }
        }

        // Determines whether the given type is supported for materialization
        private void CheckInitializerType(Type type)
        {
            // nominal types are not supported
            TypeUsage typeUsage;
            if (_funcletizer.RootContext.Perspective.TryGetType(type, out typeUsage))
            {
                var typeKind = typeUsage.EdmType.BuiltInTypeKind;
                if (BuiltInTypeKind.EntityType == typeKind
                    ||
                    BuiltInTypeKind.ComplexType == typeKind)
                {
                    throw new NotSupportedException(
                        Strings.ELinq_UnsupportedNominalType(
                            typeUsage.EdmType.FullName));
                }
            }

            // types implementing IEnumerable are not supported
            if (TypeSystem.IsSequenceType(type))
            {
                throw new NotSupportedException(
                    Strings.ELinq_UnsupportedEnumerableType(
                        DescribeClrType(type)));
            }
        }

        // requires: Left and right are non-null.
        // effects: Determines if the given types are equivalent, ignoring facets. In
        // the case of primitive types, consider types equivalent if their kinds are 
        // equivalent.
        // comments: This method is useful in cases where the type facets or specific
        // store primitive type are not reliably known, e.g. when the EDM type is determined 
        // from the CLR type
        private static bool TypeUsageEquals(TypeUsage left, TypeUsage right)
        {
            DebugCheck.NotNull(left);
            DebugCheck.NotNull(right);
            if (left.EdmType.EdmEquals(right.EdmType))
            {
                return true;
            }

            // compare element types for collection
            if (BuiltInTypeKind.CollectionType == left.EdmType.BuiltInTypeKind
                &&
                BuiltInTypeKind.CollectionType == right.EdmType.BuiltInTypeKind)
            {
                return TypeUsageEquals(
                    ((CollectionType)left.EdmType).TypeUsage,
                    ((CollectionType)right.EdmType).TypeUsage);
            }

            // special case for primitive types
            if (BuiltInTypeKind.PrimitiveType == left.EdmType.BuiltInTypeKind
                &&
                BuiltInTypeKind.PrimitiveType == right.EdmType.BuiltInTypeKind)
            {
                // since LINQ expressions cannot indicate model types directly, we must
                // consider types equivalent if they match on the given CLR equivalent
                // types (consider the Xml and String primitive types)
                return ((PrimitiveType)left.EdmType).ClrEquivalentType.Equals(
                    ((PrimitiveType)right.EdmType).ClrEquivalentType);
            }

            return false;
        }

        private TypeUsage GetValueLayerType(Type linqType)
        {
            TypeUsage type;
            if (!TryGetValueLayerType(linqType, out type))
            {
                throw new NotSupportedException(Strings.ELinq_UnsupportedType(linqType));
            }
            return type;
        }

        // Determine C-Space equivalent type for linqType
        private bool TryGetValueLayerType(Type linqType, out TypeUsage type)
        {
            // Remove nullable
            var nonNullableType = TypeSystem.GetNonNullableType(linqType);

            // Enum types are only supported for EDM V3 and higher, do not force loading
            // enum types for previous versions of EDM
            if (nonNullableType.IsEnum() && this.EdmItemCollection.EdmVersion < XmlConstants.EdmVersionForV3)
            {
                nonNullableType = nonNullableType.GetEnumUnderlyingType();
            }

            // See if this is a primitive type
            PrimitiveTypeKind primitiveTypeKind;
            if (ClrProviderManifest.TryGetPrimitiveTypeKind(nonNullableType, out primitiveTypeKind))
            {
                type = EdmProviderManifest.Instance.GetCanonicalModelTypeUsage(primitiveTypeKind);
                return true;
            }

            // See if this is a collection type (if so, recursively resolve)
            var elementType = TypeSystem.GetElementType(nonNullableType);
            if (elementType != nonNullableType)
            {
                TypeUsage elementTypeUsage;
                if (TryGetValueLayerType(elementType, out elementTypeUsage))
                {
                    type = TypeHelpers.CreateCollectionTypeUsage(elementTypeUsage);
                    return true;
                }
            }

            // Ensure the metadata for this object type is loaded
            _perspective.MetadataWorkspace.ImplicitLoadAssemblyForType(linqType, null);

            if (!_perspective.TryGetTypeByName(nonNullableType.FullNameWithNesting(), false, out type))
            {
                // If the user is casting to a type that is not a model type or a primitive type it can be a cast to an enum that
                // is not in the model. In that case we use the underlying enum type. 
                // Note that if the underlying type is not any of the EF primitive types we will fail with and InvalidCastException.
                // This is consistent with what we would do when seeing a cast to a primitive type that is not a EF valid primitive 
                // type (e.g. ulong).
                if (nonNullableType.IsEnum()
                    && ClrProviderManifest.TryGetPrimitiveTypeKind(nonNullableType.GetEnumUnderlyingType(), out primitiveTypeKind))
                {
                    type = EdmProviderManifest.Instance.GetCanonicalModelTypeUsage(primitiveTypeKind);
                }
            }

            return type != null;
        }

        // <summary>
        // Utility method validating type for comparison ops (isNull, equals, etc.).
        // Only primitive types, entity types, and simple row types (no IGrouping/EntityCollection) are
        // supported.
        // </summary>
        private static void VerifyTypeSupportedForComparison(Type clrType, TypeUsage edmType, Stack<EdmMember> memberPath, bool isNullComparison)
        {
            // NOTE: due to bug in null handling for complex types, complex types are currently not supported
            // for comparisons (see SQL BU 543956)
            switch (edmType.EdmType.BuiltInTypeKind)
            {
                case BuiltInTypeKind.PrimitiveType:
                case BuiltInTypeKind.EnumType:
                case BuiltInTypeKind.EntityType:
                case BuiltInTypeKind.RefType:
                    return;

                case BuiltInTypeKind.RowType:
                    {
                        InitializerMetadata initializerMetadata;
                        if (!InitializerMetadata.TryGetInitializerMetadata(edmType, out initializerMetadata)
                            ||
                            initializerMetadata.Kind == InitializerMetadataKind.ProjectionInitializer
                            ||
                            initializerMetadata.Kind == InitializerMetadataKind.ProjectionNew)
                        {
                            if (!isNullComparison)
                            {
                                VerifyRowTypeSupportedForComparison(clrType, (RowType)edmType.EdmType, memberPath, isNullComparison);
                            }
                            return;
                        }
                        break;
                    }
                default:
                    break;
            }

            if (null == memberPath)
            {
                throw new NotSupportedException(Strings.ELinq_UnsupportedComparison(DescribeClrType(clrType)));
            }
            else
            {
                // build up description of member path
                var memberPathDescription = new StringBuilder();
                foreach (var member in memberPath)
                {
                    memberPathDescription.Append(Strings.ELinq_UnsupportedRowMemberComparison(member.Name));
                }
                memberPathDescription.Append(Strings.ELinq_UnsupportedRowTypeComparison(DescribeClrType(clrType)));
                throw new NotSupportedException(Strings.ELinq_UnsupportedRowComparison(memberPathDescription.ToString()));
            }
        }

        private static void VerifyRowTypeSupportedForComparison(Type clrType, RowType rowType, Stack<EdmMember> memberPath, bool isNullComparison)
        {
            foreach (EdmMember member in rowType.Properties)
            {
                if (null == memberPath)
                {
                    memberPath = new Stack<EdmMember>();
                }
                memberPath.Push(member);
                VerifyTypeSupportedForComparison(clrType, member.TypeUsage, memberPath, isNullComparison);
                memberPath.Pop();
            }
        }

        // <summary>
        // Describe type for exception message.
        // </summary>
        internal static string DescribeClrType(Type clrType)
        {
            // Yes, this is a heuristic... just a best effort way of getting
            // a reasonable exception message
            if (IsCSharpGeneratedClass(clrType.Name, "DisplayClass")
                || IsVBGeneratedClass(clrType.Name, "Closure"))
            {
                return Strings.ELinq_ClosureType;
            }
            if (IsCSharpGeneratedClass(clrType.Name, "AnonymousType")
                || IsVBGeneratedClass(clrType.Name, "AnonymousType"))
            {
                return Strings.ELinq_AnonymousType;
            }

            return clrType.FullName;
        }

        private static bool IsCSharpGeneratedClass(string typeName, string pattern)
        {
            return typeName.Contains("<>") && typeName.Contains("__") && typeName.Contains(pattern);
        }

        private static bool IsVBGeneratedClass(string typeName, string pattern)
        {
            return typeName.Contains("_") && typeName.Contains("$") && typeName.Contains(pattern);
        }

        // <summary>
        // Creates an implementation of IsNull. Throws exception when operand type is not supported.
        // </summary>
        private static DbExpression CreateIsNullExpression(DbExpression operand, Type operandClrType)
        {
            VerifyTypeSupportedForComparison(operandClrType, operand.ResultType, null, true);
            return operand.IsNull();
        }

        // <summary>
        // Creates an implementation of equals using the given pattern. Throws exception when argument types
        // are not supported for equals comparison.
        // </summary>
        private DbExpression CreateEqualsExpression(
            DbExpression left, DbExpression right, EqualsPattern pattern, Type leftClrType, Type rightClrType)
        {
            VerifyTypeSupportedForComparison(leftClrType, left.ResultType, null, false);
            VerifyTypeSupportedForComparison(rightClrType, right.ResultType, null, false);

            //For Ref Type comparison, check whether they refer to compatible Entity Types.
            var leftType = left.ResultType;
            var rightType = right.ResultType;
            if (leftType.EdmType.BuiltInTypeKind == BuiltInTypeKind.RefType
                && rightType.EdmType.BuiltInTypeKind == BuiltInTypeKind.RefType)
            {
                TypeUsage commonType;
                if (!TypeSemantics.TryGetCommonType(leftType, rightType, out commonType))
                {
                    var leftRefType = left.ResultType.EdmType as RefType;
                    var rightRefType = right.ResultType.EdmType as RefType;
                    throw new NotSupportedException(
                        Strings.ELinq_UnsupportedRefComparison(leftRefType.ElementType.FullName, rightRefType.ElementType.FullName));
                }
            }

            return RecursivelyRewriteEqualsExpression(left, right, pattern);
        }

        private DbExpression RecursivelyRewriteEqualsExpression(DbExpression left, DbExpression right, EqualsPattern pattern)
        {
            // check if either side is an initializer type
            var leftType = left.ResultType.EdmType as RowType;
            var rightType = right.ResultType.EdmType as RowType;

            if (null != leftType
                || null != rightType)
            {
                if (null != leftType && null != rightType)
                {
                    DbExpression shreddedEquals = null;
                    // if the types are the same, use struct equivalence semantics
                    foreach (var property in leftType.Properties)
                    {
                        var leftElement = left.Property(property);
                        var rightElement = right.Property(property);
                        var elementsEquals = RecursivelyRewriteEqualsExpression(
                            leftElement, rightElement, pattern);

                        // build up and expression
                        if (null == shreddedEquals)
                        {
                            shreddedEquals = elementsEquals;
                        }
                        else
                        {
                            shreddedEquals = shreddedEquals.And(elementsEquals);
                        }
                    }
                    return shreddedEquals;
                }
                else
                {
                    // if one or both sides is an initializer and the types are not the same,
                    // "equals" always evaluates to false
                    return DbExpressionBuilder.False;
                }
            }
            else
            {
                return
                    _funcletizer.RootContext.ContextOptions.UseCSharpNullComparisonBehavior
                        ? ImplementEquality(left, right, EqualsPattern.Store)
                        : ImplementEquality(left, right, pattern);
            }
        }

        // For comparisons, where the left and right side are nullable or not nullable, 
        // here are the (compositionally safe) null equality predicates:
        // -- x NOT NULL, y NULL
        // x = y AND  NOT (y IS NULL)
        // -- x NULL, y NULL
        // (x = y AND  (NOT (x IS NULL OR y IS NULL))) OR (x IS NULL AND y IS NULL)
        // -- x NOT NULL, y NOT NULL
        // x = y
        // -- x NULL, y NOT NULL
        // x = y AND  NOT (x IS NULL)
        private DbExpression ImplementEquality(DbExpression left, DbExpression right, EqualsPattern pattern)
        {
            switch (left.ExpressionKind)
            {
                case DbExpressionKind.Constant:
                    switch (right.ExpressionKind)
                    {
                        case DbExpressionKind.Constant: // constant EQ constant
                            return left.Equal(right);
                        case DbExpressionKind.Null: // null EQ constant --> false
                            return DbExpressionBuilder.False;
                        default:
                            return ImplementEqualityConstantAndUnknown((DbConstantExpression)left, right, pattern);
                    }
                case DbExpressionKind.Null:
                    switch (right.ExpressionKind)
                    {
                        case DbExpressionKind.Constant: // null EQ constant --> false
                            return DbExpressionBuilder.False;
                        case DbExpressionKind.Null: // null EQ null --> true
                            return DbExpressionBuilder.True;
                        default: // null EQ right --> right IS NULL
                            return right.IsNull();
                    }
                default: // unknown
                    switch (right.ExpressionKind)
                    {
                        case DbExpressionKind.Constant:
                            return ImplementEqualityConstantAndUnknown((DbConstantExpression)right, left, pattern);
                        case DbExpressionKind.Null: //  left EQ null --> left IS NULL
                            return left.IsNull();
                        default:
                            return ImplementEqualityUnknownArguments(left, right, pattern);
                    }
            }
        }

        // Generate an equality expression with one unknown operator and 
        private DbExpression ImplementEqualityConstantAndUnknown(
            DbConstantExpression constant, DbExpression unknown, EqualsPattern pattern)
        {
            switch (pattern)
            {
                case EqualsPattern.Store:
                case EqualsPattern.PositiveNullEqualityNonComposable: // for Joins                    
                    return constant.Equal(unknown); // either both are non-null, or one is null and the predicate result is undefined
                case EqualsPattern.PositiveNullEqualityComposable:
                    if (!_funcletizer.RootContext.ContextOptions.UseCSharpNullComparisonBehavior)
                    {
                        return constant.Equal(unknown); // same as EqualsPattern.PositiveNullEqualityNonComposable
                    }
                    return constant.Equal(unknown).And(unknown.IsNull().Not());
                // add more logic to avoid undefined result for true clr semantics
                default:
                    Debug.Fail("unknown pattern");
                    return null;
            }
        }

        // Generate an equality expression where the values of the left and right operands are completely unknown 
        private DbExpression ImplementEqualityUnknownArguments(DbExpression left, DbExpression right, EqualsPattern pattern)
        {
            switch (pattern)
            {
                case EqualsPattern.Store: // left EQ right
                    return left.Equal(right);
                case EqualsPattern.PositiveNullEqualityNonComposable: // for Joins
                    return left.Equal(right).Or(left.IsNull().And(right.IsNull()));
                case EqualsPattern.PositiveNullEqualityComposable:
                    {
                        var bothNotNull = left.Equal(right);
                        var bothNull = left.IsNull().And(right.IsNull());
                        if (!_funcletizer.RootContext.ContextOptions.UseCSharpNullComparisonBehavior)
                        {
                            return bothNotNull.Or(bothNull); // same as EqualsPattern.PositiveNullEqualityNonComposable
                        }
                        // add more logic to avoid undefined result for true clr semantics, ensuring composability
                        // (left EQ right AND NOT (left IS NULL OR right IS NULL)) OR (left IS NULL AND right IS NULL)
                        var anyOneIsNull = left.IsNull().Or(right.IsNull());
                        return (bothNotNull.And(anyOneIsNull.Not())).Or(bothNull);
                    }
                default:
                    Debug.Fail("unexpected pattern");
                    return null;
            }
        }

        #endregion

        #region Helper Methods Shared by Translators

        // <summary>
        // Helper method for String.Like
        // object.Like(likeExpression[, escapeCharacter]) is translated to:
        // object like likeExpression [escape escapeCharacter]
        // </summary>
        // <returns> The translation </returns>
        private DbExpression TranslateLike(MethodCallExpression call)
        {
            char dummyEscapeChar;
            var providerSupportsEscapingLikeArgument = ProviderManifest.SupportsEscapingLikeArgument(out dummyEscapeChar);

            var inputExpression = call.Arguments[0];
            var patternExpression = call.Arguments[1];
            var escapeExpression = (call.Arguments.Count > 2 ? call.Arguments[2] : null);
            
            if (!providerSupportsEscapingLikeArgument && (escapeExpression != null))
            {
                throw new ProviderIncompatibleException(Strings.ProviderDoesNotSupportEscapingLikeArgument);
            }

            var translatedPatternExpression = TranslateExpression(patternExpression);
            var translatedEscapeExpression = (escapeExpression != null ? TranslateExpression(escapeExpression) : null);
            var translatedInputExpression = TranslateExpression(inputExpression);

            return escapeExpression != null ?
                translatedInputExpression.Like(translatedPatternExpression, translatedEscapeExpression) :
                translatedInputExpression.Like(translatedPatternExpression);
        }

        // <summary>
        // Helper method for String.StartsWith, String.EndsWith and String.Contains
        // object.Method(argument), where Method is one of String.StartsWith, String.EndsWith or
        // String.Contains is translated into:
        // 1) If argument is a constant or parameter and the provider supports escaping:
        // object like ("%") + argument1 + ("%"), where argument1 is argument escaped by the provider
        // and ("%") are appended on the beginning/end depending on whether
        // insertPercentAtStart/insertPercentAtEnd are specified
        // 2) Otherwise:
        // object.Method(argument) ->  defaultTranslator
        // </summary>
        // <param name="insertPercentAtStart"> Should '%' be inserted at the beginning of the pattern </param>
        // <param name="insertPercentAtEnd"> Should '%' be inserted at the end of the pattern </param>
        // <param name="defaultTranslator"> The delegate that provides the default translation </param>
        // <returns> The translation </returns>
        private DbExpression TranslateFunctionIntoLike(
            MethodCallExpression call, bool insertPercentAtStart, bool insertPercentAtEnd,
            Func<ExpressionConverter, MethodCallExpression, DbExpression, DbExpression, DbExpression> defaultTranslator)
        {
            char escapeChar;
            var providerSupportsEscapingLikeArgument = ProviderManifest.SupportsEscapingLikeArgument(out escapeChar);
            var useLikeTranslation = false;
            var specifyEscape = true;

            var patternExpression = call.Arguments[0];
            var inputExpression = call.Object;

            var queryParameterExpression = patternExpression as QueryParameterExpression;
            if (providerSupportsEscapingLikeArgument && (queryParameterExpression != null))
            {
                useLikeTranslation = true;

                var methodInfo = typeof(ExpressionConverter).GetMethod("PreparePattern", BindingFlags.Static | BindingFlags.NonPublic);
                var inputPrm = Expression.Parameter(typeof(string), "input");
                var preparePatternFunc = Expression.Lambda<Func<string, Tuple<string, bool>>>(
                    Expression.Call(
                        methodInfo, 
                        inputPrm, 
                        Expression.Constant(insertPercentAtStart), 
                        Expression.Constant(insertPercentAtEnd), 
                        Expression.Constant(ProviderManifest)),
                    inputPrm);

                patternExpression = queryParameterExpression.EscapeParameterForLike(preparePatternFunc);
            }

            var translatedPatternExpression = TranslateExpression(patternExpression);
            var translatedInputExpression = TranslateExpression(inputExpression);

            if (providerSupportsEscapingLikeArgument && translatedPatternExpression.ExpressionKind == DbExpressionKind.Constant)
            {
                useLikeTranslation = true;
                var constantExpression = (DbConstantExpression)translatedPatternExpression;

                var preparedPattern = PreparePattern(
                    (string)constantExpression.Value, insertPercentAtStart, insertPercentAtEnd, ProviderManifest);

                Debug.Assert(preparedPattern.Item1 != null, "The prepared value should not be null when the input is non-null");

                var preparedValue = preparedPattern.Item1;
                specifyEscape = preparedPattern.Item2;

                //Note: the result type needs to be taken from the original expression, as the user may have specified Unicode/Non-Unicode
                translatedPatternExpression = constantExpression.ResultType.Constant(preparedValue);
            }

            DbExpression result;
            if (useLikeTranslation)
            {
                if (specifyEscape)
                {
                    //DevDiv #326720: The constant expression for the escape character should not have unicode set by default
                    var escapeExpression =
                        EdmProviderManifest.Instance.GetCanonicalModelTypeUsage(PrimitiveTypeKind.String).Constant(
                            new String(new[] { escapeChar }));
                    result = translatedInputExpression.Like(translatedPatternExpression, escapeExpression);
                }
                else
                {
                    result = translatedInputExpression.Like(translatedPatternExpression);
                }
            }
            else
            {
                result = defaultTranslator(this, call, translatedPatternExpression, translatedInputExpression);
            }

            return result;
        }

        // <summary>
        // Prepare the given input patternValue into a pattern to be used in a LIKE expression by
        // first escaping it by the provider and then appending "%" and the beginging/end depending
        // on whether insertPercentAtStart/insertPercentAtEnd is specified.
        // </summary>
        private static Tuple<string, bool> PreparePattern(string patternValue, bool insertPercentAtStart, bool insertPercentAtEnd, DbProviderManifest providerManifest)
        {
            // Dev10 #800466: The pattern value if originating from a parameter value could be null
            if (patternValue == null)
            {
                return new Tuple<string, bool>(null, false);
            }

            var escapedPatternValue = providerManifest.EscapeLikeArgument(patternValue);

            if (escapedPatternValue == null)
            {
                throw new ProviderIncompatibleException(Strings.ProviderEscapeLikeArgumentReturnedNull);
            }

            var specifyEscape = patternValue != escapedPatternValue;

            var patternBuilder = new StringBuilder();
            if (insertPercentAtStart)
            {
                patternBuilder.Append("%");
            }
            patternBuilder.Append(escapedPatternValue);
            if (insertPercentAtEnd)
            {
                patternBuilder.Append("%");
            }

            return new Tuple<string, bool>(patternBuilder.ToString(), specifyEscape);
        }

        // <summary>
        // Translates the arguments into DbExpressions
        // and creates a canonical function with the given functionName and these arguments
        // </summary>
        // <param name="functionName"> Should represent a non-aggregate canonical function </param>
        // <param name="Expression"> Passed only for error handling purposes </param>
        private DbFunctionExpression TranslateIntoCanonicalFunction(
            string functionName, Expression Expression, params Expression[] linqArguments)
        {
            var translatedArguments = new DbExpression[linqArguments.Length];
            for (var i = 0; i < linqArguments.Length; i++)
            {
                translatedArguments[i] = TranslateExpression(linqArguments[i]);
            }
            return CreateCanonicalFunction(functionName, Expression, translatedArguments);
        }

        // <summary>
        // Creates a canonical function with the given name and the given arguments
        // </summary>
        // <param name="functionName"> Should represent a non-aggregate canonical function </param>
        // <param name="Expression"> Passed only for error handling purposes </param>
        private DbFunctionExpression CreateCanonicalFunction(
            string functionName, Expression Expression, params DbExpression[] translatedArguments)
        {
            var translatedArgumentTypes = new List<TypeUsage>(translatedArguments.Length);
            foreach (var translatedArgument in translatedArguments)
            {
                translatedArgumentTypes.Add(translatedArgument.ResultType);
            }
            var function = FindCanonicalFunction(functionName, translatedArgumentTypes, false /* isGroupAggregateFunction */, Expression);
            return function.Invoke(translatedArguments);
        }

        // <summary>
        // Finds a canonical function with the given functionName and argumentTypes
        // </summary>
        private EdmFunction FindCanonicalFunction(
            string functionName, IList<TypeUsage> argumentTypes, bool isGroupAggregateFunction, Expression Expression)
        {
            return FindFunction(EdmNamespaceName, functionName, argumentTypes, isGroupAggregateFunction, Expression);
        }

        // <summary>
        // Finds a function with the given namespaceName, functionName and argumentTypes
        // </summary>
        private EdmFunction FindFunction(
            string namespaceName, string functionName, IList<TypeUsage> argumentTypes, bool isGroupAggregateFunction, Expression Expression)
        {
            // find the function
            IList<EdmFunction> candidateFunctions;
            if (!_perspective.TryGetFunctionByName(namespaceName, functionName, false /* ignore case */, out candidateFunctions))
            {
                ThrowUnresolvableFunction(Expression);
            }

            Debug.Assert(null != candidateFunctions && candidateFunctions.Count > 0, "provider functions must not be null or empty");

            bool isAmbiguous;
            var function = FunctionOverloadResolver.ResolveFunctionOverloads(
                candidateFunctions, argumentTypes, isGroupAggregateFunction, out isAmbiguous);
            if (isAmbiguous || null == function)
            {
                ThrowUnresolvableFunctionOverload(Expression, isAmbiguous);
            }
            return function;
        }

        // <summary>
        // Helper method for FindFunction
        // </summary>
        private static void ThrowUnresolvableFunction(Expression Expression)
        {
            if (Expression.NodeType
                == ExpressionType.Call)
            {
                var methodInfo = ((MethodCallExpression)Expression).Method;
                throw new NotSupportedException(Strings.ELinq_UnresolvableFunctionForMethod(methodInfo, methodInfo.DeclaringType));
            }
            else if (Expression.NodeType
                     == ExpressionType.MemberAccess)
            {
                string memberName;
                Type memberType;
                var memberInfo = TypeSystem.PropertyOrField(((MemberExpression)Expression).Member, out memberName, out memberType);
                throw new NotSupportedException(Strings.ELinq_UnresolvableFunctionForMember(memberInfo, memberInfo.DeclaringType));
            }
            throw new NotSupportedException(Strings.ELinq_UnresolvableFunctionForExpression(Expression.NodeType));
        }

        // <summary>
        // Helper method for FindCanonicalFunction
        // </summary>
        private static void ThrowUnresolvableFunctionOverload(Expression Expression, bool isAmbiguous)
        {
            if (Expression.NodeType
                == ExpressionType.Call)
            {
                var methodInfo = ((MethodCallExpression)Expression).Method;
                if (isAmbiguous)
                {
                    throw new NotSupportedException(
                        Strings.ELinq_UnresolvableFunctionForMethodAmbiguousMatch(methodInfo, methodInfo.DeclaringType));
                }
                else
                {
                    throw new NotSupportedException(
                        Strings.ELinq_UnresolvableFunctionForMethodNotFound(methodInfo, methodInfo.DeclaringType));
                }
            }
            else if (Expression.NodeType
                     == ExpressionType.MemberAccess)
            {
                string memberName;
                Type memberType;
                var memberInfo = TypeSystem.PropertyOrField(((MemberExpression)Expression).Member, out memberName, out memberType);
                throw new NotSupportedException(Strings.ELinq_UnresolvableStoreFunctionForMember(memberInfo, memberInfo.DeclaringType));
            }
            throw new NotSupportedException(Strings.ELinq_UnresolvableStoreFunctionForExpression(Expression.NodeType));
        }

        private static DbNewInstanceExpression CreateNewRowExpression(
            List<KeyValuePair<string, DbExpression>> columns, InitializerMetadata initializerMetadata)
        {
            var propertyValues = new List<DbExpression>(columns.Count);
            var properties = new List<EdmProperty>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                propertyValues.Add(column.Value);
                properties.Add(new EdmProperty(column.Key, column.Value.ResultType));
            }
            var rowType = new RowType(properties, initializerMetadata);
            var typeUsage = TypeUsage.Create(rowType);
            return typeUsage.New(propertyValues);
        }

        #endregion

        #region Private enums

        // Describes different implementation pattern for equality comparisons.
        // For all patterns, if one side of the expression is a constant null, converts to an IS NULL
        // expression (or resolves to 'true' or 'false' if some constraint is known for the other side).
        // 
        // If neither side is a constant null, the semantics differ:
        //
        // (1) (left EQ right) => left and right are equal and not null, so return true.
        // (2) (left IS NULL AND right IS NULL) => Both left and right are null, so return true.
        // (3) NOT (left IS NULL OR right IS NULL) =>
        //      If only one of left or right is null, (1) evaluates to "unknown" and (2) evaluates to false. So we get "unknown" from DB which is null in C#.
        //      This is not desired as it does not help in composability. Hence, (3) is used to return false instead of "unknown" when only one of the operands is null.
        //
        // Store: (1)
        // PositiveNullEqualityNonComposable: (1) OR (2) - suitable only for Join operators, as they are not composable
        // PositiveNullEqualityComposable: (1) OR (2) AND (3)
        //
        // In the actual implementation (see ImplementEquality), optimizations exist if one or the other
        // side is known not to be null.
        private enum EqualsPattern
        {
            Store, // defer to store
            PositiveNullEqualityNonComposable,
            // simulate C# semantics in store, return "null" if left or right is null, but not both. Suitable for joins.
            PositiveNullEqualityComposable, // simulate C# semantics in store, always return true or false
        }

        #endregion
    }
}
