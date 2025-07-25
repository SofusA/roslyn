﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

/*
SPEC:

Type inference occurs as part of the compile-time processing of a method invocation
and takes place before the overload resolution step of the invocation. When a
particular method group is specified in a method invocation, and no type arguments
are specified as part of the method invocation, type inference is applied to each
generic method in the method group. If type inference succeeds, then the inferred
type arguments are used to determine the types of formal parameters for subsequent 
overload resolution. If overload resolution chooses a generic method as the one to
invoke then the inferred type arguments are used as the actual type arguments for the
invocation. If type inference for a particular method fails, that method does not
participate in overload resolution. The failure of type inference, in and of itself,
does not cause a compile-time error. However, it often leads to a compile-time error
when overload resolution then fails to find any applicable methods.

If the supplied number of arguments is different than the number of parameters in
the method, then inference immediately fails. Otherwise, assume that the generic
method has the following signature:

Tr M<X1...Xn>(T1 x1 ... Tm xm)

With a method call of the form M(E1...Em) the task of type inference is to find
unique type arguments S1...Sn for each of the type parameters X1...Xn so that the
call M<S1...Sn>(E1...Em)becomes valid.

During the process of inference each type parameter Xi is either fixed to a particular
type Si or unfixed with an associated set of bounds. Each of the bounds is some type T.
Each bound is classified as an upper bound, lower bound or exact bound.
Initially each type variable Xi is unfixed with an empty set of bounds.

*/

// This file contains the implementation for method type inference on calls (with
// arguments, and method type inference on conversion of method groups to delegate
// types (which will not have arguments.)

namespace Microsoft.CodeAnalysis.CSharp
{
    internal static class PooledDictionaryIgnoringNullableModifiersForReferenceTypes
    {
        private static readonly ObjectPool<PooledDictionary<NamedTypeSymbol, NamedTypeSymbol>> s_poolInstance
            = PooledDictionary<NamedTypeSymbol, NamedTypeSymbol>.CreatePool(Symbols.SymbolEqualityComparer.IgnoringNullable);

        internal static PooledDictionary<NamedTypeSymbol, NamedTypeSymbol> GetInstance()
        {
            var instance = s_poolInstance.Allocate();
            Debug.Assert(instance.Count == 0);
            return instance;
        }
    }

    // Method type inference can fail, but we still might have some best guesses. 
    internal readonly struct MethodTypeInferenceResult
    {
        public readonly ImmutableArray<TypeWithAnnotations> InferredTypeArguments;
        /// <summary>
        /// At least one type argument was inferred from a function type.
        /// </summary>
        public readonly bool HasTypeArgumentInferredFromFunctionType;
        public readonly bool Success;

        public MethodTypeInferenceResult(
            bool success,
            ImmutableArray<TypeWithAnnotations> inferredTypeArguments,
            bool hasTypeArgumentInferredFromFunctionType)
        {
            this.Success = success;
            this.InferredTypeArguments = inferredTypeArguments;
            this.HasTypeArgumentInferredFromFunctionType = hasTypeArgumentInferredFromFunctionType;
        }
    }

    internal struct MethodTypeInferrer
    {
        internal abstract class Extensions
        {
            internal static readonly Extensions Default = new DefaultExtensions();

            internal abstract TypeWithAnnotations GetTypeWithAnnotations(BoundExpression expr);

            internal abstract TypeWithAnnotations GetMethodGroupResultType(BoundMethodGroup group, MethodSymbol method);

            private sealed class DefaultExtensions : Extensions
            {
                internal override TypeWithAnnotations GetTypeWithAnnotations(BoundExpression expr)
                {
                    return TypeWithAnnotations.Create(expr.GetTypeOrFunctionType());
                }

                internal override TypeWithAnnotations GetMethodGroupResultType(BoundMethodGroup group, MethodSymbol method)
                {
                    return method.ReturnTypeWithAnnotations;
                }
            }
        }

        private enum InferenceResult
        {
            InferenceFailed,
            MadeProgress,
            NoProgress,
            Success
        }

        private enum Dependency
        {
            Unknown = 0x00,
            NotDependent = 0x01,
            DependsMask = 0x10,
            Direct = 0x11,
            Indirect = 0x12
        }

        private readonly CSharpCompilation _compilation;
        private readonly ConversionsBase _conversions;
        private readonly ImmutableArray<TypeParameterSymbol> _methodTypeParameters;
        private readonly NamedTypeSymbol _constructedContainingTypeOfMethod;
        private readonly ImmutableArray<TypeWithAnnotations> _formalParameterTypes;
        private readonly ImmutableArray<RefKind> _formalParameterRefKinds;
        private readonly ImmutableArray<BoundExpression> _arguments;
        private readonly Extensions _extensions;

        // When doing type inference on a new extension method, we combine the type parameters
        // from the extension declaration and from the method, so we cannot rely on the ordinals from the type parameters.
        private readonly Dictionary<TypeParameterSymbol, int> _ordinals;

        private readonly (TypeWithAnnotations Type, bool FromFunctionType)[] _fixedResults;
        private readonly HashSet<TypeWithAnnotations>[] _exactBounds;
        private readonly HashSet<TypeWithAnnotations>[] _upperBounds;
        private readonly HashSet<TypeWithAnnotations>[] _lowerBounds;

        // https://github.com/dotnet/csharplang/blob/main/proposals/csharp-9.0/nullable-reference-types-specification.md#fixing
        // If the resulting candidate is a reference type and *all* of the exact bounds or *any* of
        // the lower bounds are nullable reference types, `null` or `default`, then `?` is added to
        // the resulting candidate, making it a nullable reference type.
        //
        // This set of bounds effectively tracks whether a typeless null expression (i.e. null
        // literal) was used as an argument to a parameter whose type is one of this method's type
        // parameters. Because such expressions only occur as by-value inputs, we only need to track
        // the lower bounds, not the exact or upper bounds.
        private readonly NullableAnnotation[] _nullableAnnotationLowerBounds;

        private Dependency[,] _dependencies; // Initialized lazily
        private bool _dependenciesDirty;

        /// <summary>
        /// For error recovery, we allow a mismatch between the number of arguments and parameters
        /// during type inference. This sometimes enables inferring the type for a lambda parameter.
        /// </summary>
        private int NumberArgumentsToProcess => System.Math.Min(_arguments.Length, _formalParameterTypes.Length);

        public static MethodTypeInferenceResult Infer(
            Binder binder,
            ConversionsBase conversions,
            ImmutableArray<TypeParameterSymbol> methodTypeParameters,
            // We are attempting to build a map from method type parameters 
            // to inferred type arguments.

            NamedTypeSymbol constructedContainingTypeOfMethod,
            ImmutableArray<TypeWithAnnotations> formalParameterTypes,

            // We have some unusual requirements for the types that flow into the inference engine.
            // Consider the following inference problems:
            // 
            // Scenario one: 
            //
            // class C<T> 
            // {
            //   delegate Y FT<X, Y>(T t, X x);
            //   static void M<U, V>(U u, FT<U, V> f);
            //   ...
            //   C<double>.M(123, (t,x)=>t+x);
            //
            // From the first argument we infer that U is int. How now must we make an inference on
            // the second argument? The *declared* type of the formal is C<T>.FT<U,V>. The
            // actual type at the time of inference is known to be C<double>.FT<int, something>
            // where "something" is to be determined by inferring the return type of the 
            // lambda by determine the type of "double + int". 
            // 
            // Therefore when we do type inference, if a formal parameter type is a generic delegate
            // then *its* formal parameter types must be the formal parameter types of the 
            // *constructed* generic delegate C<double>.FT<...>, not C<T>.FT<...>. 
            //
            // One would therefore suppose that we'd expect the formal parameter types to here
            // be passed in with the types constructed with the information known from the
            // call site, not the declared types.
            //
            // Contrast that with this scenario:
            //
            // Scenario Two:
            //
            // interface I<T> 
            // { 
            //    void M<U>(T t, U u); 
            // }
            // ...
            // static void Goo<V>(V v, I<V> iv) 
            // {
            //   iv.M(v, "");
            // }
            //
            // Obviously inference should succeed here; it should infer that U is string. 
            //
            // But consider what happens during the inference process on the first argument.
            // The first thing we will do is say "what's the type of the argument? V. What's
            // the type of the corresponding formal parameter? The first formal parameter of
            // I<V>.M<whatever> is of type V. The inference engine will then say "V is a 
            // method type parameter, and therefore we have an inference from V to V". 
            // But *V* is not one of the method type parameters being inferred; the only 
            // method type parameter being inferred here is *U*.
            //
            // This is perhaps some evidence that the formal parameters passed in should be
            // the formal parameters of the *declared* method; in this case, (T, U), not
            // the formal parameters of the *constructed* method, (V, U). 
            //
            // However, one might make the argument that no, we could just add a check
            // to ensure that if we see a method type parameter as a formal parameter type,
            // then we only perform the inference if the method type parameter is a type 
            // parameter of the method for which inference is being performed.
            //
            // Unfortunately, that does not work either:
            //
            // Scenario three:
            //
            // class C<T>
            // {
            //   static void M<U>(T t, U u)
            //   {
            //     ...
            //     C<U>.M(u, 123);
            //     ...
            //   }
            // }
            //
            // The *original* formal parameter types are (T, U); the *constructed* formal parameter types
            // are (U, U), but *those are logically two different U's*. The first U is from the outer caller;
            // the second U is the U of the recursive call.
            //
            // That is, suppose someone called C<string>.M<double>(string, double).  The recursive call should be to
            // C<double>.M<int>(double, int). We should absolutely not make an inference on the first argument
            // from U to U just because C<U>.M<something>'s first formal parameter is of type U.  If we did then
            // inference would fail, because we'd end up with two bounds on 'U' -- 'U' and 'int'. We only want
            // the latter bound.
            //
            // What these three scenarios show is that for a "normal" inference we need to have the
            // formal parameters of the *original* method definition, but when making an inference from a lambda
            // to a delegate, we need to have the *constructed* method signature in order that the formal
            // parameters *of the delegate* be correct.
            //
            // How to solve this problem?
            //
            // We solve it by passing in the formal parameters in their *original* form, but also getting
            // the *fully constructed* type that the method call is on. When constructing the fixed
            // delegate type for inference from a lambda, we do the appropriate type substitution on
            // the delegate.

            ImmutableArray<RefKind> formalParameterRefKinds, // Optional; assume all value if missing.
            ImmutableArray<BoundExpression> arguments,// Required; in scenarios like method group conversions where there are
                                                      // no arguments per se we cons up some fake arguments.
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            Extensions extensions = null,
            // Map of TypeParameterSymbol to ordinal for new extension methods
            Dictionary<TypeParameterSymbol, int> ordinals = null)
        {
            Debug.Assert(!methodTypeParameters.IsDefault);
            Debug.Assert(methodTypeParameters.Length > 0);
            Debug.Assert(!formalParameterTypes.IsDefault);
            Debug.Assert(formalParameterRefKinds.IsDefault || formalParameterRefKinds.Length == formalParameterTypes.Length);
            Debug.Assert(!arguments.IsDefault);

            // Early out: if the method has no formal parameters then we know that inference will fail.
            if (formalParameterTypes.Length == 0)
            {
                return new MethodTypeInferenceResult(success: false, inferredTypeArguments: default, hasTypeArgumentInferredFromFunctionType: false);

                // UNDONE: OPTIMIZATION: We could check to see whether there is a type
                // UNDONE: parameter which is never used in any formal parameter; if
                // UNDONE: so then we know ahead of time that inference will fail.
            }

            var inferrer = new MethodTypeInferrer(
                binder.Compilation,
                conversions,
                methodTypeParameters,
                constructedContainingTypeOfMethod,
                formalParameterTypes,
                formalParameterRefKinds,
                arguments,
                extensions,
                ordinals);
            return inferrer.InferTypeArgs(binder, ref useSiteInfo);
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Fixed, unfixed and bounded type parameters
        //
        // SPEC: During the process of inference each type parameter is either fixed to
        // SPEC: a particular type or unfixed with an associated set of bounds. Each of
        // SPEC: the bounds is of some type T. Initially each type parameter is unfixed
        // SPEC: with an empty set of bounds.

#nullable enable
        private MethodTypeInferrer(
            CSharpCompilation compilation,
            ConversionsBase conversions,
            ImmutableArray<TypeParameterSymbol> methodTypeParameters,
            NamedTypeSymbol constructedContainingTypeOfMethod,
            ImmutableArray<TypeWithAnnotations> formalParameterTypes,
            ImmutableArray<RefKind> formalParameterRefKinds,
            ImmutableArray<BoundExpression> arguments,
            Extensions extensions,
            Dictionary<TypeParameterSymbol, int>? ordinals)
        {
            _compilation = compilation;
            _conversions = conversions;
            _methodTypeParameters = methodTypeParameters;
            _constructedContainingTypeOfMethod = constructedContainingTypeOfMethod;
            _formalParameterTypes = formalParameterTypes;
            _formalParameterRefKinds = formalParameterRefKinds;
            _arguments = arguments;
            _extensions = extensions ?? Extensions.Default;

            // For extension members, we do inference across all the type parameters (from the extension declaration and the member)
            Debug.Assert(ordinals is null || ordinals.Values.Count() == ordinals.Values.Distinct().Count());
            Debug.Assert(ordinals is null || methodTypeParameters.All(tp => ordinals.ContainsKey(tp)));

            _ordinals = ordinals;
            _fixedResults = new (TypeWithAnnotations, bool)[methodTypeParameters.Length];
            _exactBounds = new HashSet<TypeWithAnnotations>[methodTypeParameters.Length];
            _upperBounds = new HashSet<TypeWithAnnotations>[methodTypeParameters.Length];
            _lowerBounds = new HashSet<TypeWithAnnotations>[methodTypeParameters.Length];
            _nullableAnnotationLowerBounds = new NullableAnnotation[methodTypeParameters.Length];
            Debug.Assert(_nullableAnnotationLowerBounds.All(annotation => annotation.IsNotAnnotated()));
            _dependencies = null;
            _dependenciesDirty = false;
        }
#nullable enable

#if DEBUG

        internal string Dump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Method type inference internal state");
            sb.AppendFormat("Inferring method type parameters <{0}>\n", string.Join(", ", _methodTypeParameters));
            sb.Append("Formal parameter types (");
            for (int i = 0; i < _formalParameterTypes.Length; ++i)
            {
                if (i != 0)
                {
                    sb.Append(", ");
                }

                sb.Append(GetRefKind(i).ToParameterPrefix());
                sb.Append(_formalParameterTypes[i]);
            }

            sb.Append('\n');

            sb.AppendFormat("Argument types ({0})\n", string.Join(", ", from a in _arguments select a.Type));

            if (_dependencies == null)
            {
                sb.AppendLine("Dependencies are not yet calculated");
            }
            else
            {
                sb.AppendFormat("Dependencies are {0}\n", _dependenciesDirty ? "out of date" : "up to date");
                sb.AppendLine("dependency matrix (Not dependent / Direct / Indirect / Unknown):");
                for (int i = 0; i < _methodTypeParameters.Length; ++i)
                {
                    for (int j = 0; j < _methodTypeParameters.Length; ++j)
                    {
                        switch (_dependencies[i, j])
                        {
                            case Dependency.NotDependent:
                                sb.Append('N');
                                break;
                            case Dependency.Direct:
                                sb.Append('D');
                                break;
                            case Dependency.Indirect:
                                sb.Append('I');
                                break;
                            case Dependency.Unknown:
                                sb.Append('U');
                                break;
                        }
                    }
                    sb.AppendLine();
                }
            }

            for (int i = 0; i < _methodTypeParameters.Length; ++i)
            {
                sb.AppendFormat("Method type parameter {0}: ", _methodTypeParameters[i].Name);

                var fixedType = _fixedResults[i].Type;

                if (!fixedType.HasType)
                {
                    sb.Append("UNFIXED ");
                }
                else
                {
                    sb.AppendFormat("FIXED to {0} ", fixedType);
                }

                sb.AppendFormat("upper bounds: ({0}) ", (_upperBounds[i] == null) ? "" : string.Join(", ", _upperBounds[i]));
                sb.AppendFormat("lower bounds: ({0}) ", (_lowerBounds[i] == null) ? "" : string.Join(", ", _lowerBounds[i]));
                sb.AppendFormat("exact bounds: ({0}) ", (_exactBounds[i] == null) ? "" : string.Join(", ", _exactBounds[i]));
                sb.AppendLine();
            }

            return sb.ToString();
        }

#endif

        private RefKind GetRefKind(int index)
        {
            Debug.Assert(0 <= index && index < _formalParameterTypes.Length);
            return _formalParameterRefKinds.IsDefault ? RefKind.None : _formalParameterRefKinds[index];
        }

        private ImmutableArray<TypeWithAnnotations> GetResults(out bool inferredFromFunctionType)
        {
            // Anything we didn't infer a type for, give the error type.
            // Note: the error type will have the same name as the name
            // of the type parameter we were trying to infer.  This will give a
            // nice user experience where by we will show something like
            // the following:
            //
            // user types: customers.Select(
            // we show   : IE<TResult> IE<Customer>.Select<Customer,TResult>(Func<Customer,TResult> selector)
            //
            // Initially we thought we'd just show ?.  i.e.:
            //
            //  IE<?> IE<Customer>.Select<Customer,?>(Func<Customer,?> selector)
            //
            // This is nice and concise.  However, it falls down if there are multiple
            // type params that we have left.

            for (int i = 0; i < _methodTypeParameters.Length; i++)
            {
                var fixedResultType = _fixedResults[i].Type;
                if (fixedResultType.HasType)
                {
                    if (!fixedResultType.Type.IsErrorType())
                    {
                        if (_conversions.IncludeNullability && _nullableAnnotationLowerBounds[i].IsAnnotated())
                        {
                            _fixedResults[i] = _fixedResults[i] with { Type = fixedResultType.AsAnnotated() };
                        }
                        continue;
                    }

                    var errorTypeName = fixedResultType.Type.Name;
                    if (errorTypeName != null)
                    {
                        continue;
                    }
                }
                _fixedResults[i] = (TypeWithAnnotations.Create(new ExtendedErrorTypeSymbol(_constructedContainingTypeOfMethod, _methodTypeParameters[i].Name, 0, null, false)), false);
            }

            return GetInferredTypeArguments(out inferredFromFunctionType);
        }

        private bool ValidIndex(int index)
        {
            return 0 <= index && index < _methodTypeParameters.Length;
        }

        private bool IsUnfixed(int methodTypeParameterIndex)
        {
            Debug.Assert(ValidIndex(methodTypeParameterIndex));
            return !_fixedResults[methodTypeParameterIndex].Type.HasType;
        }

        private bool IsUnfixedTypeParameter(TypeWithAnnotations type)
        {
            Debug.Assert(type.HasType);

            if (type.TypeKind != TypeKind.TypeParameter) return false;

            TypeParameterSymbol typeParameter = (TypeParameterSymbol)type.Type;
            int ordinal = GetOrdinal(typeParameter);
            return ValidIndex(ordinal) &&
                TypeSymbol.Equals(typeParameter, _methodTypeParameters[ordinal], TypeCompareKind.ConsiderEverything2) &&
                IsUnfixed(ordinal);
        }

        private int GetOrdinal(TypeParameterSymbol typeParameter)
        {
            if (_ordinals != null)
            {
                return _ordinals[typeParameter];
            }

            return typeParameter.Ordinal;
        }

        private bool AllFixed()
        {
            for (int methodTypeParameterIndex = 0; methodTypeParameterIndex < _methodTypeParameters.Length; ++methodTypeParameterIndex)
            {
                if (IsUnfixed(methodTypeParameterIndex))
                {
                    return false;
                }
            }
            return true;
        }

        private void AddBound(TypeWithAnnotations addedBound, HashSet<TypeWithAnnotations>[] collectedBounds, TypeWithAnnotations methodTypeParameterWithAnnotations)
        {
            Debug.Assert(IsUnfixedTypeParameter(methodTypeParameterWithAnnotations));

            var methodTypeParameter = (TypeParameterSymbol)methodTypeParameterWithAnnotations.Type;
            int methodTypeParameterIndex = GetOrdinal(methodTypeParameter);

            if (collectedBounds[methodTypeParameterIndex] == null)
            {
                collectedBounds[methodTypeParameterIndex] = new HashSet<TypeWithAnnotations>(TypeWithAnnotations.EqualsComparer.ConsiderEverythingComparer);
            }

            collectedBounds[methodTypeParameterIndex].Add(addedBound);
        }

        private bool HasBound(int methodTypeParameterIndex)
        {
            Debug.Assert(ValidIndex(methodTypeParameterIndex));
            return _lowerBounds[methodTypeParameterIndex] != null ||
                _upperBounds[methodTypeParameterIndex] != null ||
                _exactBounds[methodTypeParameterIndex] != null;
        }

        private TypeSymbol GetFixedDelegateOrFunctionPointer(TypeSymbol delegateOrFunctionPointerType)
        {
            Debug.Assert((object)delegateOrFunctionPointerType != null);
            Debug.Assert(delegateOrFunctionPointerType.IsDelegateType() || delegateOrFunctionPointerType is FunctionPointerTypeSymbol);

            // We have a delegate where the input types use no unfixed parameters.  Create
            // a substitution context; we can substitute unfixed parameters for themselves
            // since they don't actually occur in the inputs.  (They may occur in the outputs,
            // or there may be input parameters fixed to _unfixed_ method type variables.
            // Both of those scenarios are legal.)

            var fixedArguments = _methodTypeParameters.SelectAsArray(
                static (typeParameter, i, self) => self.IsUnfixed(i) ? TypeWithAnnotations.Create(typeParameter) : self._fixedResults[i].Type,
                this);
            TypeMap typeMap = new TypeMap(_constructedContainingTypeOfMethod, _methodTypeParameters, fixedArguments);
            return typeMap.SubstituteType(delegateOrFunctionPointerType).Type;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Phases
        //

        private MethodTypeInferenceResult InferTypeArgs(Binder binder, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: Type inference takes place in phases. Each phase will try to infer type 
            // SPEC: arguments for more type parameters based on the findings of the previous
            // SPEC: phase. The first phase makes some initial inferences of bounds, whereas
            // SPEC: the second phase fixes type parameters to specific types and infers further
            // SPEC: bounds. The second phase may have to be repeated a number of times.
            InferTypeArgsFirstPhase(binder, ref useSiteInfo);
            bool success = InferTypeArgsSecondPhase(binder, ref useSiteInfo);
            var inferredTypeArguments = GetResults(out bool inferredFromFunctionType);
            return new MethodTypeInferenceResult(success, inferredTypeArguments, inferredFromFunctionType);
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // The first phase
        //

        private void InferTypeArgsFirstPhase(Binder binder, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(!_formalParameterTypes.IsDefault);
            Debug.Assert(!_arguments.IsDefault);

            // We expect that we have been handed a list of arguments and a list of the 
            // formal parameter types they correspond to; all the details about named and 
            // optional parameters have already been dealt with.

            // SPEC: For each of the method arguments Ei:
            for (int arg = 0, length = this.NumberArgumentsToProcess; arg < length; arg++)
            {
                BoundExpression argument = _arguments[arg];
                TypeWithAnnotations target = _formalParameterTypes[arg];
                ExactOrBoundsKind kind = GetRefKind(arg).IsManagedReference() || target.Type.IsPointerType() ? ExactOrBoundsKind.Exact : ExactOrBoundsKind.LowerBound;

                MakeExplicitParameterTypeInferences(binder, argument, target, kind, ref useSiteInfo);
            }
        }

#nullable enable
        private void MakeExplicitParameterTypeInferences(Binder binder, BoundExpression argument, TypeWithAnnotations target, ExactOrBoundsKind kind, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: * If Ei is an anonymous function, and Ti is a delegate type or expression tree type,
            // SPEC:   an explicit type parameter inference is made from Ei to Ti and
            // SPEC:   an explicit return type inference is made from Ei to Ti.

            // (We cannot make an output type inference from a method group
            // at this time because we have no fixed types yet to use for
            // overload resolution.)

            // SPEC: * Otherwise, if Ei has a type U then a lower-bound inference 
            // SPEC:   or exact inference is made from U to Ti.

            // SPEC: * Otherwise, no inference is made for this argument

            if (argument.Kind == BoundKind.UnboundLambda && target.Type.GetDelegateType() is { })
            {
                ExplicitParameterTypeInference(argument, target, ref useSiteInfo);
                ExplicitReturnTypeInference(argument, target, ref useSiteInfo);
            }
            else if (argument.Kind == BoundKind.UnconvertedCollectionExpression)
            {
                MakeCollectionExpressionTypeInferences(binder, (BoundUnconvertedCollectionExpression)argument, target, kind, ref useSiteInfo);
            }
            else if (argument.Kind != BoundKind.TupleLiteral ||
                !MakeExplicitParameterTypeInferences(binder, (BoundTupleLiteral)argument, target, kind, ref useSiteInfo))
            {
                // Either the argument is not a tuple literal, or we were unable to do the inference from its elements, let's try to infer from argument type
                var argumentType = _extensions.GetTypeWithAnnotations(argument);
                if (IsReallyAType(argumentType.Type))
                {
                    ExactOrBoundsInference(kind, argumentType, target, ref useSiteInfo);
                }
                else if (IsUnfixedTypeParameter(target) && !target.NullableAnnotation.IsAnnotated() && kind is ExactOrBoundsKind.LowerBound)
                {
                    var ordinal = GetOrdinal((TypeParameterSymbol)target.Type);
                    _nullableAnnotationLowerBounds[ordinal] = _nullableAnnotationLowerBounds[ordinal].Join(argumentType.NullableAnnotation);
                }
            }
        }

        private void MakeCollectionExpressionTypeInferences(
            Binder binder,
            BoundUnconvertedCollectionExpression argument,
            TypeWithAnnotations target,
            ExactOrBoundsKind kind,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            TypeSymbol targetType = target.Type;
            Debug.Assert(targetType is { });
            if (targetType is null)
            {
                return;
            }

            if (argument.Elements.Length == 0)
            {
                return;
            }

            if (!binder.TryGetCollectionIterationType((ExpressionSyntax)argument.Syntax, targetType.StrippedType(), out TypeWithAnnotations targetElementType))
            {
                return;
            }

            foreach (var element in argument.Elements)
            {
                if (element is BoundCollectionExpressionSpreadElement spread)
                {
                    MakeSpreadElementTypeInferences(spread, targetElementType, ref useSiteInfo);
                }
                else
                {
                    MakeExplicitParameterTypeInferences(binder, (BoundExpression)element, targetElementType, kind, ref useSiteInfo);
                }
            }
        }

        private void MakeSpreadElementTypeInferences(
            BoundCollectionExpressionSpreadElement argument,
            TypeWithAnnotations target,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(target.Type is { });
            if (target.Type is null)
            {
                return;
            }

            var enumeratorInfo = argument.EnumeratorInfoOpt;
            if (enumeratorInfo is null)
            {
                return;
            }

            LowerBoundInference(enumeratorInfo.ElementTypeWithAnnotations, target, ref useSiteInfo);
        }
#nullable disable

        private bool MakeExplicitParameterTypeInferences(Binder binder, BoundTupleLiteral argument, TypeWithAnnotations target, ExactOrBoundsKind kind, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // try match up element-wise to the destination tuple (or underlying type)
            // Example:
            //      if   "(a: 1, b: "qq")" is passed as   (T, U) arg
            //      then T becomes int and U becomes string
            if (target.Type.Kind != SymbolKind.NamedType)
            {
                // tuples can only match to tuples or tuple underlying types.
                return false;
            }

            var destination = (NamedTypeSymbol)target.Type;
            var sourceArguments = argument.Arguments;

            // check if the type is actually compatible type for a tuple of given cardinality
            if (!destination.IsTupleTypeOfCardinality(sourceArguments.Length))
            {
                // target is not a tuple of appropriate shape
                return false;
            }

            var destTypes = destination.TupleElementTypesWithAnnotations;
            Debug.Assert(sourceArguments.Length == destTypes.Length);

            // NOTE: we are losing tuple element names when recursing into argument expressions.
            //       that is ok, because we are inferring type parameters used in the matching elements, 
            //       This is not the situation where entire tuple literal is used to infer a single type param

            for (int i = 0; i < sourceArguments.Length; i++)
            {
                var sourceArgument = sourceArguments[i];
                var destType = destTypes[i];
                MakeExplicitParameterTypeInferences(binder, sourceArgument, destType, kind, ref useSiteInfo);
            }

            return true;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // The second phase
        //

        private bool InferTypeArgsSecondPhase(Binder binder, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: The second phase proceeds as follows:
            // SPEC: * If no unfixed type parameters exist then type inference succeeds.
            // SPEC: * Otherwise, if there exists one or more arguments Ei with corresponding
            // SPEC:   parameter type Ti such that:
            // SPEC:     o the output type of Ei with type Ti contains at least one unfixed
            // SPEC:       type parameter Xj, and
            // SPEC:     o none of the input types of Ei with type Ti contains any unfixed
            // SPEC:       type parameter Xj, 
            // SPEC:   then an output type inference is made from all such Ei to Ti. 
            // SPEC: * Whether or not the previous step actually made an inference, we must
            // SPEC:   now fix at least one type parameter, as follows:
            // SPEC: * If there exists one or more type parameters Xi such that 
            // SPEC:     o Xi is unfixed, and
            // SPEC:     o Xi has a non-empty set of bounds, and
            // SPEC:     o Xi does not depend on any Xj 
            // SPEC:   then each such Xi is fixed. If any fixing operation fails then type
            // SPEC:   inference fails.
            // SPEC: * Otherwise, if there exists one or more type parameters Xi such that
            // SPEC:     o Xi is unfixed, and
            // SPEC:     o Xi has a non-empty set of bounds, and
            // SPEC:     o there is at least one type parameter Xj that depends on Xi
            // SPEC:   then each such Xi is fixed. If any fixing operation fails then
            // SPEC:   type inference fails.
            // SPEC: * Otherwise, we are unable to make progress and there are unfixed parameters.
            // SPEC:   Type inference fails. 
            // SPEC: * If type inference neither succeeds nor fails then the second phase is
            // SPEC:   repeated until type inference succeeds or fails. (Since each repetition of
            // SPEC:   the second phase either succeeds, fails or fixes an unfixed type parameter,
            // SPEC:   the algorithm must terminate with no more repetitions than the number
            // SPEC:   of type parameters.

            InitializeDependencies();
            while (true)
            {
                var res = DoSecondPhase(binder, ref useSiteInfo);
                Debug.Assert(res != InferenceResult.NoProgress);
                if (res == InferenceResult.InferenceFailed)
                {
                    return false;
                }
                if (res == InferenceResult.Success)
                {
                    return true;
                }
                // Otherwise, we made some progress last time; do it again.
            }
        }

        private InferenceResult DoSecondPhase(Binder binder, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: * If no unfixed type parameters exist then type inference succeeds.
            if (AllFixed())
            {
                return InferenceResult.Success;
            }
            // SPEC: * Otherwise, if there exists one or more arguments Ei with
            // SPEC:   corresponding parameter type Ti such that:
            // SPEC:     o the output type of Ei with type Ti contains at least one unfixed
            // SPEC:       type parameter Xj, and
            // SPEC:     o none of the input types of Ei with type Ti contains any unfixed
            // SPEC:       type parameter Xj,
            // SPEC:   then an output type inference is made from all such Ei to Ti.

            MakeOutputTypeInferences(binder, ref useSiteInfo);

            // SPEC: * Whether or not the previous step actually made an inference, we
            // SPEC:   must now fix at least one type parameter, as follows:
            // SPEC: * If there exists one or more type parameters Xi such that
            // SPEC:     o Xi is unfixed, and
            // SPEC:     o Xi has a non-empty set of bounds, and
            // SPEC:     o Xi does not depend on any Xj
            // SPEC:   then each such Xi is fixed. If any fixing operation fails then
            // SPEC:   type inference fails.

            InferenceResult res;
            res = FixNondependentParameters(ref useSiteInfo);
            if (res != InferenceResult.NoProgress)
            {
                return res;
            }
            // SPEC: * Otherwise, if there exists one or more type parameters Xi such that
            // SPEC:     o Xi is unfixed, and
            // SPEC:     o Xi has a non-empty set of bounds, and
            // SPEC:     o there is at least one type parameter Xj that depends on Xi
            // SPEC:   then each such Xi is fixed. If any fixing operation fails then
            // SPEC:   type inference fails.
            res = FixDependentParameters(ref useSiteInfo);
            if (res != InferenceResult.NoProgress)
            {
                return res;
            }
            // SPEC: * Otherwise, we are unable to make progress and there are
            // SPEC:   unfixed parameters. Type inference fails.
            return InferenceResult.InferenceFailed;
        }

        private void MakeOutputTypeInferences(Binder binder, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: Otherwise, for all arguments Ei with corresponding parameter type Ti
            // SPEC: where the output types contain unfixed type parameters but the input
            // SPEC: types do not, an output type inference is made from Ei to Ti.

            for (int arg = 0, length = this.NumberArgumentsToProcess; arg < length; arg++)
            {
                var formalType = _formalParameterTypes[arg];
                var argument = _arguments[arg];
                MakeOutputTypeInferences(binder, argument, formalType, ref useSiteInfo);
            }
        }

        private void MakeOutputTypeInferences(Binder binder, BoundExpression argument, TypeWithAnnotations formalType, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (argument.Kind == BoundKind.TupleLiteral && (object)argument.Type == null)
            {
                MakeOutputTypeInferences(binder, (BoundTupleLiteral)argument, formalType, ref useSiteInfo);
            }
            else if (argument.Kind == BoundKind.UnconvertedCollectionExpression)
            {
                MakeOutputTypeInferences(binder, (BoundUnconvertedCollectionExpression)argument, formalType, ref useSiteInfo);
            }
            else
            {
                if (HasUnfixedParamInOutputType(argument, formalType.Type) && !HasUnfixedParamInInputType(argument, formalType.Type))
                {
                    //UNDONE: if (argument->isTYPEORNAMESPACEERROR() && argumentType->IsErrorType())
                    //UNDONE: {
                    //UNDONE:     argumentType = GetTypeManager().GetErrorSym();
                    //UNDONE: }
                    OutputTypeInference(binder, argument, formalType, ref useSiteInfo);
                }
            }
        }

        private void MakeOutputTypeInferences(Binder binder, BoundUnconvertedCollectionExpression argument, TypeWithAnnotations formalType, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (argument.Elements.Length == 0)
            {
                return;
            }

            if (!binder.TryGetCollectionIterationType((ExpressionSyntax)argument.Syntax, formalType.Type, out TypeWithAnnotations targetElementType))
            {
                return;
            }

            foreach (var element in argument.Elements)
            {
                if (element is BoundExpression expression)
                {
                    MakeOutputTypeInferences(binder, expression, targetElementType, ref useSiteInfo);
                }
            }
        }

        private void MakeOutputTypeInferences(Binder binder, BoundTupleLiteral argument, TypeWithAnnotations formalType, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (formalType.Type.Kind != SymbolKind.NamedType)
            {
                // tuples can only match to tuples or tuple underlying types.
                return;
            }

            var destination = (NamedTypeSymbol)formalType.Type;

            Debug.Assert((object)argument.Type == null, "should not need to dig into elements if tuple has natural type");
            var sourceArguments = argument.Arguments;

            // check if the type is actually compatible type for a tuple of given cardinality
            if (!destination.IsTupleTypeOfCardinality(sourceArguments.Length))
            {
                return;
            }

            var destTypes = destination.TupleElementTypesWithAnnotations;
            Debug.Assert(sourceArguments.Length == destTypes.Length);

            for (int i = 0; i < sourceArguments.Length; i++)
            {
                var sourceArgument = sourceArguments[i];
                var destType = destTypes[i];
                MakeOutputTypeInferences(binder, sourceArgument, destType, ref useSiteInfo);
            }
        }

        private InferenceResult FixNondependentParameters(ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: * Otherwise, if there exists one or more type parameters Xi such that
            // SPEC:     o Xi is unfixed, and
            // SPEC:     o Xi has a non-empty set of bounds, and
            // SPEC:     o Xi does not depend on any Xj
            // SPEC:   then each such Xi is fixed.
            return FixParameters((ref MethodTypeInferrer inferrer, int index) => !inferrer.DependsOnAny(index), ref useSiteInfo);
        }

        private InferenceResult FixDependentParameters(ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: * All unfixed type parameters Xi are fixed for which all of the following hold:
            // SPEC:   * There is at least one type parameter Xj that depends on Xi.
            // SPEC:   * Xi has a non-empty set of bounds.
            return FixParameters((ref MethodTypeInferrer inferrer, int index) => inferrer.AnyDependsOn(index), ref useSiteInfo);
        }

        private delegate bool FixParametersPredicate(ref MethodTypeInferrer inferrer, int index);

        private InferenceResult FixParameters(
            FixParametersPredicate predicate,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // Dependency is only defined for unfixed parameters. Therefore, fixing
            // a parameter may cause all of its dependencies to become no longer
            // dependent on anything. We need to first determine which parameters need to be 
            // fixed, and then fix them all at once.

            var needsFixing = BitVector.Create(_methodTypeParameters.Length);
            var result = InferenceResult.NoProgress;
            for (int param = 0; param < _methodTypeParameters.Length; param++)
            {
                if (IsUnfixed(param) && HasBound(param) && predicate(ref this, param))
                {
                    needsFixing[param] = true;
                    result = InferenceResult.MadeProgress;
                }
            }

            for (int param = 0; param < _methodTypeParameters.Length; param++)
            {
                // Fix as much as you can, even if there are errors.  That will
                // help with intellisense.
                if (needsFixing[param])
                {
                    if (!Fix(param, ref useSiteInfo))
                    {
                        result = InferenceResult.InferenceFailed;
                    }
                }
            }
            return result;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Input types
        //
        private static bool DoesInputTypeContain(BoundExpression argument, TypeSymbol formalParameterType, TypeParameterSymbol typeParameter)
        {
            // SPEC: If E is a method group or an anonymous function and T is a delegate
            // SPEC: type or expression tree type then all the parameter types of T are
            // SPEC: input types of E with type T.

            var delegateOrFunctionPointerType = formalParameterType.GetDelegateOrFunctionPointerType();
            if ((object)delegateOrFunctionPointerType == null)
            {
                return false; // No input types.
            }

            var isFunctionPointer = delegateOrFunctionPointerType.IsFunctionPointer();
            if ((isFunctionPointer && argument.Kind != BoundKind.UnconvertedAddressOfOperator) ||
                (!isFunctionPointer && argument.Kind is not (BoundKind.UnboundLambda or BoundKind.MethodGroup)))
            {
                return false; // No input types.
            }

            var parameters = delegateOrFunctionPointerType.DelegateOrFunctionPointerParameters();
            if (parameters.IsDefaultOrEmpty)
            {
                return false;
            }

            foreach (var parameter in parameters)
            {
                if (parameter.Type.ContainsTypeParameter(typeParameter))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasUnfixedParamInInputType(BoundExpression pSource, TypeSymbol pDest)
        {
            for (int iParam = 0; iParam < _methodTypeParameters.Length; iParam++)
            {
                if (IsUnfixed(iParam))
                {
                    if (DoesInputTypeContain(pSource, pDest, _methodTypeParameters[iParam]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Output types
        //
        private static bool DoesOutputTypeContain(BoundExpression argument, TypeSymbol formalParameterType,
            TypeParameterSymbol typeParameter)
        {
            // SPEC: If E is a method group or an anonymous function and T is a delegate
            // SPEC: type or expression tree type then the return type of T is an output type
            // SPEC: of E with type T.

            var delegateOrFunctionPointerType = formalParameterType.GetDelegateOrFunctionPointerType();
            if ((object)delegateOrFunctionPointerType == null)
            {
                return false;
            }

            var isFunctionPointer = delegateOrFunctionPointerType.IsFunctionPointer();
            if ((isFunctionPointer && argument.Kind != BoundKind.UnconvertedAddressOfOperator) ||
                (!isFunctionPointer && argument.Kind is not (BoundKind.UnboundLambda or BoundKind.MethodGroup)))
            {
                return false;
            }

            MethodSymbol method = delegateOrFunctionPointerType switch
            {
                NamedTypeSymbol n => n.DelegateInvokeMethod,
                FunctionPointerTypeSymbol f => f.Signature,
                _ => throw ExceptionUtilities.UnexpectedValue(delegateOrFunctionPointerType)
            };

            if ((object)method == null || method.HasUseSiteError)
            {
                return false;
            }

            var returnType = method.ReturnType;
            if ((object)returnType == null)
            {
                return false;
            }

            return returnType.ContainsTypeParameter(typeParameter);
        }

        private bool HasUnfixedParamInOutputType(BoundExpression argument, TypeSymbol formalParameterType)
        {
            for (int iParam = 0; iParam < _methodTypeParameters.Length; iParam++)
            {
                if (IsUnfixed(iParam))
                {
                    if (DoesOutputTypeContain(argument, formalParameterType, _methodTypeParameters[iParam]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Dependence
        //

        private bool DependsDirectlyOn(int iParam, int jParam)
        {
            Debug.Assert(ValidIndex(iParam));
            Debug.Assert(ValidIndex(jParam));

            // SPEC: An unfixed type parameter Xi depends directly on an unfixed type
            // SPEC: parameter Xj if for some argument Ek with type Tk, Xj occurs
            // SPEC: in an input type of Ek and Xi occurs in an output type of Ek
            // SPEC: with type Tk.

            // We compute and record the Depends Directly On relationship once, in
            // InitializeDependencies, below.

            // At this point, everything should be unfixed.

            Debug.Assert(IsUnfixed(iParam));
            Debug.Assert(IsUnfixed(jParam));

            for (int iArg = 0, length = this.NumberArgumentsToProcess; iArg < length; iArg++)
            {
                var formalParameterType = _formalParameterTypes[iArg].Type;
                var argument = _arguments[iArg];
                if (DoesInputTypeContain(argument, formalParameterType, _methodTypeParameters[jParam]) &&
                    DoesOutputTypeContain(argument, formalParameterType, _methodTypeParameters[iParam]))
                {
                    return true;
                }
            }
            return false;
        }

        private void InitializeDependencies()
        {
            // We track dependencies by a two-d square array that gives the known
            // relationship between every pair of type parameters. The relationship
            // is one of:
            //
            // * Unknown relationship
            // * known to be not dependent
            // * known to depend directly
            // * known to depend indirectly
            //
            // Since dependency is only defined on unfixed type parameters, fixing a type
            // parameter causes all dependencies involving that parameter to go to
            // the "known to be not dependent" state. Since dependency is a transitive property,
            // this means that doing so may require recalculating the indirect dependencies
            // from the now possibly smaller set of dependencies.
            //
            // Therefore, when we detect that the dependency state has possibly changed
            // due to fixing, we change all "depends indirectly" back into "unknown" and
            // recalculate from the remaining "depends directly".
            //
            // This algorithm thereby yields an extremely bad (but extremely unlikely) worst
            // case for asymptotic performance. Suppose there are n type parameters.
            // "DependsTransitivelyOn" below costs O(n) because it must potentially check
            // all n type parameters to see if there is any k such that Xj => Xk => Xi.
            // "DeduceDependencies" calls "DependsTransitivelyOn" for each "Unknown"
            // pair, and there could be O(n^2) such pairs, so DependsTransitivelyOn is
            // worst-case O(n^3).  And we could have to recalculate the dependency graph
            // after each type parameter is fixed in turn, so that would be O(n) calls to
            // DependsTransitivelyOn, giving this algorithm a worst case of O(n^4).
            //
            // Of course, in reality, n is going to almost always be on the order of
            // "smaller than 5", and there will not be O(n^2) dependency relationships 
            // between type parameters; it is far more likely that the transitivity chains
            // will be very short and not branch or loop at all. This is much more likely to
            // be an O(n^2) algorithm in practice.

            Debug.Assert(_dependencies == null);
            _dependencies = new Dependency[_methodTypeParameters.Length, _methodTypeParameters.Length];
            int iParam;
            int jParam;
            Debug.Assert(0 == (int)Dependency.Unknown);
            for (iParam = 0; iParam < _methodTypeParameters.Length; ++iParam)
            {
                for (jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
                {
                    if (DependsDirectlyOn(iParam, jParam))
                    {
                        _dependencies[iParam, jParam] = Dependency.Direct;
                    }
                }
            }

            DeduceAllDependencies();
        }

        private bool DependsOn(int iParam, int jParam)
        {
            Debug.Assert(_dependencies != null);

            // SPEC: Xj depends on Xi if Xj depends directly on Xi, or if Xi depends
            // SPEC: directly on Xk and Xk depends on Xj. Thus "depends on" is the
            // SPEC: transitive but not reflexive closure of "depends directly on".

            Debug.Assert(0 <= iParam && iParam < _methodTypeParameters.Length);
            Debug.Assert(0 <= jParam && jParam < _methodTypeParameters.Length);

            if (_dependenciesDirty)
            {
                SetIndirectsToUnknown();
                DeduceAllDependencies();
            }
            return 0 != ((_dependencies[iParam, jParam]) & Dependency.DependsMask);
        }

        private bool DependsTransitivelyOn(int iParam, int jParam)
        {
            Debug.Assert(_dependencies != null);
            Debug.Assert(ValidIndex(iParam));
            Debug.Assert(ValidIndex(jParam));

            // Can we find Xk such that Xi depends on Xk and Xk depends on Xj?
            // If so, then Xi depends indirectly on Xj.  (Note that there is
            // a minor optimization here -- the spec comment above notes that
            // we want Xi to depend DIRECTLY on Xk, and Xk to depend directly
            // or indirectly on Xj. But if we already know that Xi depends
            // directly OR indirectly on Xk and Xk depends on Xj, then that's
            // good enough.)

            for (int kParam = 0; kParam < _methodTypeParameters.Length; ++kParam)
            {
                if (((_dependencies[iParam, kParam]) & Dependency.DependsMask) != 0 &&
                    ((_dependencies[kParam, jParam]) & Dependency.DependsMask) != 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void DeduceAllDependencies()
        {
            bool madeProgress;
            do
            {
                madeProgress = DeduceDependencies();
            } while (madeProgress);
            SetUnknownsToNotDependent();
            _dependenciesDirty = false;
        }

        private bool DeduceDependencies()
        {
            Debug.Assert(_dependencies != null);
            bool madeProgress = false;
            for (int iParam = 0; iParam < _methodTypeParameters.Length; ++iParam)
            {
                for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
                {
                    if (_dependencies[iParam, jParam] == Dependency.Unknown)
                    {
                        if (DependsTransitivelyOn(iParam, jParam))
                        {
                            _dependencies[iParam, jParam] = Dependency.Indirect;
                            madeProgress = true;
                        }
                    }
                }
            }
            return madeProgress;
        }

        private void SetUnknownsToNotDependent()
        {
            Debug.Assert(_dependencies != null);
            for (int iParam = 0; iParam < _methodTypeParameters.Length; ++iParam)
            {
                for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
                {
                    if (_dependencies[iParam, jParam] == Dependency.Unknown)
                    {
                        _dependencies[iParam, jParam] = Dependency.NotDependent;
                    }
                }
            }
        }

        private void SetIndirectsToUnknown()
        {
            Debug.Assert(_dependencies != null);
            for (int iParam = 0; iParam < _methodTypeParameters.Length; ++iParam)
            {
                for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
                {
                    if (_dependencies[iParam, jParam] == Dependency.Indirect)
                    {
                        _dependencies[iParam, jParam] = Dependency.Unknown;
                    }
                }
            }
        }

        ////////////////////////////////////////////////////////////////////////////////
        // A fixed parameter never depends on anything, nor is depended upon by anything.

        private void UpdateDependenciesAfterFix(int iParam)
        {
            Debug.Assert(ValidIndex(iParam));
            if (_dependencies == null)
            {
                return;
            }
            for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
            {
                _dependencies[iParam, jParam] = Dependency.NotDependent;
                _dependencies[jParam, iParam] = Dependency.NotDependent;
            }
            _dependenciesDirty = true;
        }

        private bool DependsOnAny(int iParam)
        {
            Debug.Assert(ValidIndex(iParam));
            for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
            {
                if (DependsOn(iParam, jParam))
                {
                    return true;
                }
            }
            return false;
        }

        private bool AnyDependsOn(int iParam)
        {
            Debug.Assert(ValidIndex(iParam));
            for (int jParam = 0; jParam < _methodTypeParameters.Length; ++jParam)
            {
                if (DependsOn(jParam, iParam))
                {
                    return true;
                }
            }
            return false;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Output type inferences
        //
        ////////////////////////////////////////////////////////////////////////////////

        private void OutputTypeInference(Binder binder, BoundExpression expression, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(expression != null);
            Debug.Assert(target.HasType);
            // SPEC: An output type inference is made from an expression E to a type T
            // SPEC: in the following way:

            // SPEC: * If E is an anonymous function with inferred return type U and
            // SPEC:   T is a delegate type or expression tree with return type Tb
            // SPEC:   then a lower bound inference is made from U to Tb.
            if (InferredReturnTypeInference(expression, target, ref useSiteInfo))
            {
                return;
            }
            // SPEC: * Otherwise, if E is a method group and T is a delegate type or
            // SPEC:   expression tree type with parameter types T1...Tk and return
            // SPEC:   type Tb and overload resolution of E with the types T1...Tk
            // SPEC:   yields a single method with return type U then a lower-bound
            // SPEC:   inference is made from U to Tb.
            if (MethodGroupReturnTypeInference(binder, expression, target.Type, ref useSiteInfo))
            {
                return;
            }
            // SPEC: * Otherwise, if E is an expression with type U then a lower-bound
            // SPEC:   inference is made from U to T.
            var sourceType = _extensions.GetTypeWithAnnotations(expression);
            if (sourceType.HasType)
            {
                LowerBoundInference(sourceType, target, ref useSiteInfo);
            }
            // SPEC: * Otherwise, no inferences are made.
        }

        private bool InferredReturnTypeInference(BoundExpression source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source != null);
            Debug.Assert(target.HasType);
            // SPEC: * If E is an anonymous function with inferred return type U and
            // SPEC:   T is a delegate type or expression tree with return type Tb
            // SPEC:   then a lower bound inference is made from U to Tb.

            var delegateType = target.Type.GetDelegateType();
            if ((object)delegateType == null)
            {
                return false;
            }

            // cannot be hit, because an invalid delegate does not have an unfixed return type
            // this will be checked earlier.
            Debug.Assert((object)delegateType.DelegateInvokeMethod != null && !delegateType.DelegateInvokeMethod.HasUseSiteError,
                         "This method should only be called for valid delegate types.");
            var returnType = delegateType.DelegateInvokeMethod.ReturnTypeWithAnnotations;
            if (!returnType.HasType || returnType.SpecialType == SpecialType.System_Void)
            {
                return false;
            }

            var inferredReturnType = InferReturnType(source, delegateType, ref useSiteInfo);
            if (!inferredReturnType.HasType)
            {
                return false;
            }

            Debug.Assert(inferredReturnType.Type is not FunctionTypeSymbol);

            LowerBoundInference(inferredReturnType, returnType, ref useSiteInfo);
            return true;
        }

        private bool MethodGroupReturnTypeInference(Binder binder, BoundExpression source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source != null);
            Debug.Assert((object)target != null);
            // SPEC: * Otherwise, if E is a method group and T is a delegate type or
            // SPEC:   expression tree type with parameter types T1...Tk and return
            // SPEC:   type Tb and overload resolution of E with the types T1...Tk
            // SPEC:   yields a single method with return type U then a lower-bound
            // SPEC:   inference is made from U to Tb.

            if (source.Kind is not (BoundKind.MethodGroup or BoundKind.UnconvertedAddressOfOperator))
            {
                return false;
            }

            var delegateOrFunctionPointerType = target.GetDelegateOrFunctionPointerType();
            if ((object)delegateOrFunctionPointerType == null)
            {
                return false;
            }

            if (delegateOrFunctionPointerType.IsFunctionPointer() != (source.Kind == BoundKind.UnconvertedAddressOfOperator))
            {
                return false;
            }

            // this part of the code is only called if the targetType has an unfixed type argument in the output 
            // type, which is not the case for invalid delegate invoke methods.
            var (method, isFunctionPointerResolution) = delegateOrFunctionPointerType switch
            {
                NamedTypeSymbol n => (n.DelegateInvokeMethod, false),
                FunctionPointerTypeSymbol f => (f.Signature, true),
                _ => throw ExceptionUtilities.UnexpectedValue(delegateOrFunctionPointerType),
            };
            Debug.Assert(method is { HasUseSiteError: false },
                         "This method should only be called for valid delegate or function pointer types");

            TypeWithAnnotations sourceReturnType = method.ReturnTypeWithAnnotations;
            if (!sourceReturnType.HasType || sourceReturnType.SpecialType == SpecialType.System_Void)
            {
                return false;
            }

            // At this point we are in the second phase; we know that all the input types are fixed.

            var fixedParameters = GetFixedDelegateOrFunctionPointer(delegateOrFunctionPointerType).DelegateOrFunctionPointerParameters();
            if (fixedParameters.IsDefault)
            {
                return false;
            }

            CallingConventionInfo callingConventionInfo = isFunctionPointerResolution
                ? new CallingConventionInfo(method.CallingConvention, ((FunctionPointerMethodSymbol)method).GetCallingConventionModifiers())
                : default;
            BoundMethodGroup originalMethodGroup = source as BoundMethodGroup ?? ((BoundUnconvertedAddressOfOperator)source).Operand;

            var returnType = MethodGroupReturnType(binder, originalMethodGroup, fixedParameters, method.RefKind, isFunctionPointerResolution, ref useSiteInfo, in callingConventionInfo);
            if (returnType.IsDefault || returnType.IsVoidType())
            {
                return false;
            }

            LowerBoundInference(returnType, sourceReturnType, ref useSiteInfo);
            return true;
        }

        private TypeWithAnnotations MethodGroupReturnType(
            Binder binder, BoundMethodGroup source,
            ImmutableArray<ParameterSymbol> delegateParameters,
            RefKind delegateRefKind,
            bool isFunctionPointerResolution,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            in CallingConventionInfo callingConventionInfo)
        {
            var analyzedArguments = AnalyzedArguments.GetInstance();
            Conversions.GetDelegateOrFunctionPointerArguments(source.Syntax, analyzedArguments, delegateParameters, binder.Compilation);

            var resolution = binder.ResolveMethodGroup(source, analyzedArguments, useSiteInfo: ref useSiteInfo,
                options: OverloadResolution.Options.IsMethodGroupConversion |
                         (isFunctionPointerResolution ? OverloadResolution.Options.IsFunctionPointerResolution : OverloadResolution.Options.None),
                acceptOnlyMethods: true, returnRefKind: delegateRefKind,
                // Since we are trying to infer the return type, it is not an input to resolving the method group
                returnType: null,
                callingConventionInfo: in callingConventionInfo);

            Debug.Assert(!resolution.IsNonMethodExtensionMember(out _));

            TypeWithAnnotations type = default;

            // The resolution could be empty (e.g. if there are no methods in the BoundMethodGroup).
            if (!resolution.IsEmpty)
            {
                var result = resolution.OverloadResolutionResult;
                if (result.Succeeded)
                {
                    type = _extensions.GetMethodGroupResultType(source, result.BestResult.Member);
                }
            }

            analyzedArguments.Free();
            resolution.Free();
            return type;
        }

#nullable enable
        ////////////////////////////////////////////////////////////////////////////////
        //
        // Explicit parameter type inferences
        //
        private void ExplicitParameterTypeInference(BoundExpression source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source != null);
            Debug.Assert(target.HasType);

            // SPEC: An explicit type parameter type inference is made from an expression
            // SPEC: E to a type T in the following way.
            // SPEC: If E is an explicitly typed anonymous function with parameter types
            // SPEC: U1...Uk and T is a delegate type or expression tree type with
            // SPEC: parameter types V1...Vk then for each Ui an exact inference is made
            // SPEC: from Ui to the corresponding Vi.

            if (source.Kind != BoundKind.UnboundLambda)
            {
                return;
            }

            UnboundLambda anonymousFunction = (UnboundLambda)source;

            if (!anonymousFunction.HasExplicitlyTypedParameterList)
            {
                return;
            }

            var delegateType = target.Type.GetDelegateType();
            if (delegateType is null)
            {
                return;
            }

            var delegateParameters = delegateType.DelegateParameters();
            if (delegateParameters.IsDefault)
            {
                return;
            }

            int size = delegateParameters.Length;
            if (anonymousFunction.ParameterCount < size)
            {
                size = anonymousFunction.ParameterCount;
            }

            // SPEC ISSUE: What should we do if there is an out/ref mismatch between an
            // SPEC ISSUE: anonymous function parameter and a delegate parameter?
            // SPEC ISSUE: The result will not be applicable no matter what, but should
            // SPEC ISSUE: we make any inferences?  This is going to be an error
            // SPEC ISSUE: ultimately, but it might make a difference for intellisense or
            // SPEC ISSUE: other analysis.

            for (int i = 0; i < size; ++i)
            {
                ExactInference(anonymousFunction.ParameterTypeWithAnnotations(i), delegateParameters[i].TypeWithAnnotations, ref useSiteInfo);
            }
        }

        private void ExplicitReturnTypeInference(BoundExpression source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source != null);
            Debug.Assert(target.HasType);

            // SPEC: An explicit type return type inference is made from an expression
            // SPEC: E to a type T in the following way.
            // SPEC: If E is an anonymous function with explicit return type Ur and
            // SPEC: T is a delegate type or expression tree type with return type Vr then
            // SPEC: an exact inference is made from Ur to Vr.

            if (source.Kind != BoundKind.UnboundLambda)
            {
                return;
            }

            UnboundLambda anonymousFunction = (UnboundLambda)source;
            if (!anonymousFunction.HasExplicitReturnType(out _, out TypeWithAnnotations anonymousFunctionReturnType))
            {
                return;
            }

            var delegateInvokeMethod = target.Type.GetDelegateType()?.DelegateInvokeMethod();
            if (delegateInvokeMethod is null)
            {
                return;
            }

            ExactInference(anonymousFunctionReturnType, delegateInvokeMethod.ReturnTypeWithAnnotations, ref useSiteInfo);
        }
#nullable disable

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Exact inferences
        //
        private void ExactInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: An exact inference from a type U to a type V is made as follows:

            // SPEC: * Otherwise, if U is the type U1? and V is the type V1? then an
            // SPEC:   exact inference is made from U to V.

            if (ExactNullableInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * If V is one of the unfixed Xi then U is added to the set of
            // SPEC:   exact bounds for Xi.
            if (ExactTypeParameterInference(source, target))
            {
                return;
            }

            // SPEC: * Otherwise, if U is an array type UE[...] and V is an array type VE[...]
            // SPEC:   of the same rank then an exact inference from UE to VE is made.
            if (ExactArrayInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * V is a Span<V1> and U is an array type U1[] or a Span<U1>
            // SPEC: * V is a ReadOnlySpan<V1> and U is an array type U1[] or a Span<U1> or ReadOnlySpan<U1>
            if (ExactSpanInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * Otherwise, if V is a constructed type C<V1...Vk> and U is a constructed
            // SPEC:   type C<U1...Uk> then an exact inference is made
            // SPEC:    from each Ui to the corresponding Vi.

            if (ExactConstructedInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // This can be valid via (where T : unmanaged) constraints
            if (ExactPointerInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * Otherwise no inferences are made.
        }

        private bool ExactTypeParameterInference(TypeWithAnnotations source, TypeWithAnnotations target)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: * If V is one of the unfixed Xi then U is added to the set of bounds
            // SPEC:   for Xi.
            if (IsUnfixedTypeParameter(target))
            {
                AddBound(source, _exactBounds, target);
                return true;
            }
            return false;
        }

        private bool ExactArrayInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: * Otherwise, if U is an array type UE[...] and V is an array type VE[...]
            // SPEC:   of the same rank then an exact inference from UE to VE is made.
            if (!source.Type.IsArray() || !target.Type.IsArray())
            {
                return false;
            }

            var arraySource = (ArrayTypeSymbol)source.Type;
            var arrayTarget = (ArrayTypeSymbol)target.Type;
            if (!arraySource.HasSameShapeAs(arrayTarget))
            {
                return false;
            }

            ExactInference(arraySource.ElementTypeWithAnnotations, arrayTarget.ElementTypeWithAnnotations, ref useSiteInfo);
            return true;
        }

        private readonly bool IsFeatureFirstClassSpanEnabled
        {
            get
            {
                // Note: when Compilation is null, we assume latest LangVersion.
                return _compilation?.IsFeatureEnabled(MessageID.IDS_FeatureFirstClassSpan) != false;
            }
        }

        private bool ExactSpanInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source is not null);
            Debug.Assert(target is not null);

            if (IsFeatureFirstClassSpanEnabled && (
                // SPEC: * V is a Span<V1> and U is an array type U1[] or a Span<U1>
                (
                    target.IsSpan() &&
                    (source.IsSZArray() || source.IsSpan())
                ) ||
                // SPEC: * V is a ReadOnlySpan<V1> and U is an array type U1[] or a Span<U1> or ReadOnlySpan<U1>
                (
                    target.IsReadOnlySpan() &&
                    (source.IsSZArray() || source.IsSpan() || source.IsReadOnlySpan())
                )
            ))
            {
                var sourceElementType = GetSpanOrSZArrayElementType(source);
                var targetElementType = GetSpanElementType(target);
                ExactInference(sourceElementType, targetElementType, ref useSiteInfo);
                return true;
            }

            return false;
        }

        private static TypeWithAnnotations GetSpanElementType(TypeSymbol type)
        {
            Debug.Assert(type.IsSpan() || type.IsReadOnlySpan());
            return ((NamedTypeSymbol)type).TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0];
        }

        private static TypeWithAnnotations GetSpanOrSZArrayElementType(TypeSymbol type)
        {
            return type is ArrayTypeSymbol arraySource
                ? arraySource.ElementTypeWithAnnotations
                : GetSpanElementType(type);
        }

        private enum ExactOrBoundsKind
        {
            Exact,
            LowerBound,
            UpperBound,
        }

        private void ExactOrBoundsInference(ExactOrBoundsKind kind, TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            switch (kind)
            {
                case ExactOrBoundsKind.Exact:
                    ExactInference(source, target, ref useSiteInfo);
                    break;
                case ExactOrBoundsKind.LowerBound:
                    LowerBoundInference(source, target, ref useSiteInfo);
                    break;
                case ExactOrBoundsKind.UpperBound:
                    UpperBoundInference(source, target, ref useSiteInfo);
                    break;
            }
        }

        private bool ExactOrBoundsNullableInference(ExactOrBoundsKind kind, TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            if (source.IsNullableType() && target.IsNullableType())
            {
                ExactOrBoundsInference(kind, ((NamedTypeSymbol)source.Type).TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0], ((NamedTypeSymbol)target.Type).TypeArgumentsWithAnnotationsNoUseSiteDiagnostics[0], ref useSiteInfo);
                return true;
            }

            if (isNullableOnly(source) && isNullableOnly(target))
            {
                ExactOrBoundsInference(kind, source.AsNotNullableReferenceType(), target.AsNotNullableReferenceType(), ref useSiteInfo);
                return true;
            }

            return false;

            // True if the type is nullable.
            static bool isNullableOnly(TypeWithAnnotations type)
                => type.NullableAnnotation.IsAnnotated();
        }

        private bool ExactNullableInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            return ExactOrBoundsNullableInference(ExactOrBoundsKind.Exact, source, target, ref useSiteInfo);
        }

        private bool LowerBoundTupleInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // NOTE: we are losing tuple element names when unwrapping tuple types to underlying types.
            //       that is ok, because we are inferring type parameters used in the matching elements, 
            //       This is not the situation where entire tuple type used to infer a single type param

            ImmutableArray<TypeWithAnnotations> sourceTypes;
            ImmutableArray<TypeWithAnnotations> targetTypes;

            if (!source.Type.TryGetElementTypesWithAnnotationsIfTupleType(out sourceTypes) ||
                !target.Type.TryGetElementTypesWithAnnotationsIfTupleType(out targetTypes) ||
                sourceTypes.Length != targetTypes.Length)
            {
                return false;
            }

            for (int i = 0; i < sourceTypes.Length; i++)
            {
                LowerBoundInference(sourceTypes[i], targetTypes[i], ref useSiteInfo);
            }

            return true;
        }

        private bool ExactConstructedInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: * Otherwise, if V is a constructed type C<V1...Vk> and U is a constructed
            // SPEC:   type C<U1...Uk> then an exact inference 
            // SPEC:   is made from each Ui to the corresponding Vi.

            var namedSource = source.Type as NamedTypeSymbol;
            if ((object)namedSource == null)
            {
                return false;
            }

            var namedTarget = target.Type as NamedTypeSymbol;
            if ((object)namedTarget == null)
            {
                return false;
            }

            if (!TypeSymbol.Equals(namedSource.OriginalDefinition, namedTarget.OriginalDefinition, TypeCompareKind.ConsiderEverything2))
            {
                return false;
            }

            ExactTypeArgumentInference(namedSource, namedTarget, ref useSiteInfo);
            return true;
        }

        private bool ExactPointerInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (source.TypeKind == TypeKind.Pointer && target.TypeKind == TypeKind.Pointer)
            {
                ExactInference(((PointerTypeSymbol)source.Type).PointedAtTypeWithAnnotations, ((PointerTypeSymbol)target.Type).PointedAtTypeWithAnnotations, ref useSiteInfo);
                return true;
            }
            else if (source.Type is FunctionPointerTypeSymbol { Signature: { ParameterCount: int sourceParameterCount } sourceSignature } &&
                     target.Type is FunctionPointerTypeSymbol { Signature: { ParameterCount: int targetParameterCount } targetSignature } &&
                     sourceParameterCount == targetParameterCount)
            {
                if (!FunctionPointerRefKindsEqual(sourceSignature, targetSignature) || !FunctionPointerCallingConventionsEqual(sourceSignature, targetSignature))
                {
                    return false;
                }

                for (int i = 0; i < sourceParameterCount; i++)
                {
                    ExactInference(sourceSignature.ParameterTypesWithAnnotations[i], targetSignature.ParameterTypesWithAnnotations[i], ref useSiteInfo);
                }

                ExactInference(sourceSignature.ReturnTypeWithAnnotations, targetSignature.ReturnTypeWithAnnotations, ref useSiteInfo);
                return true;
            }

            return false;
        }

        private static bool FunctionPointerCallingConventionsEqual(FunctionPointerMethodSymbol sourceSignature, FunctionPointerMethodSymbol targetSignature)
        {
            if (sourceSignature.CallingConvention != targetSignature.CallingConvention)
            {
                return false;
            }

            return (sourceSignature.GetCallingConventionModifiers(), targetSignature.GetCallingConventionModifiers()) switch
            {
                (null, null) => true,
                ({ } sourceModifiers, { } targetModifiers) when sourceModifiers.SetEqualsWithoutIntermediateHashSet(targetModifiers) => true,
                _ => false
            };
        }

        private static bool FunctionPointerRefKindsEqual(FunctionPointerMethodSymbol sourceSignature, FunctionPointerMethodSymbol targetSignature)
        {
            return sourceSignature.RefKind == targetSignature.RefKind
                   && (sourceSignature.ParameterRefKinds.IsDefault, targetSignature.ParameterRefKinds.IsDefault) switch
                   {
                       (true, false) or (false, true) => false,
                       (true, true) => true,
                       _ => sourceSignature.ParameterRefKinds.SequenceEqual(targetSignature.ParameterRefKinds)
                   };
        }

        private void ExactTypeArgumentInference(NamedTypeSymbol source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);
            Debug.Assert(TypeSymbol.Equals(source.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything2));

            var sourceTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
            var targetTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();

            source.GetAllTypeArguments(sourceTypeArguments, ref useSiteInfo);
            target.GetAllTypeArguments(targetTypeArguments, ref useSiteInfo);

            Debug.Assert(sourceTypeArguments.Count == targetTypeArguments.Count);

            for (int arg = 0; arg < sourceTypeArguments.Count; ++arg)
            {
                ExactInference(sourceTypeArguments[arg], targetTypeArguments[arg], ref useSiteInfo);
            }

            sourceTypeArguments.Free();
            targetTypeArguments.Free();
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Lower-bound inferences
        //
        private void LowerBoundInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: A lower-bound inference from a type U to a type V is made as follows:

            // SPEC: * Otherwise, if V is nullable type V1? and U is nullable type U1?
            // SPEC:   then an exact inference is made from U1 to V1.

            // SPEC ERROR: The spec should say "lower" here; we can safely make a lower-bound
            // SPEC ERROR: inference to nullable even though it is a generic struct.  That is,
            // SPEC ERROR: if we have M<T>(T?, T) called with (char?, int) then we can infer
            // SPEC ERROR: lower bounds of char and int, and choose int. If we make an exact
            // SPEC ERROR: inference of char then type inference fails.

            if (LowerBoundNullableInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * If V is one of the unfixed Xi then U is added to the set of 
            // SPEC:   lower bounds for Xi.

            if (LowerBoundTypeParameterInference(source, target))
            {
                return;
            }

            // SPEC: * Otherwise, if U is an array type Ue[...] and V is either an array
            // SPEC:   type Ve[...] of the same rank, or if U is a one-dimensional array
            // SPEC:   type Ue[] and V is one of IEnumerable<Ve>, ICollection<Ve> or
            // SPEC:   IList<Ve> then
            // SPEC:   * if Ue is known to be a reference type then a lower-bound inference
            // SPEC:     from Ue to Ve is made.
            // SPEC:   * otherwise an exact inference from Ue to Ve is made.

            if (LowerBoundArrayInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * V is a Span<V1> and U is an array type U1[] or a Span<U1>
            // SPEC: * V is a ReadOnlySpan<V1> and U is an array type U1[] or a Span<U1> or ReadOnlySpan<U1>
            if (LowerBoundSpanInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            // UNDONE: At this point we could also do an inference from non-nullable U
            // UNDONE: to nullable V.
            // UNDONE: 
            // UNDONE: We tried implementing lower bound nullable inference as follows:
            // UNDONE:
            // UNDONE: * Otherwise, if V is nullable type V1? and U is a non-nullable 
            // UNDONE:   struct type then an exact inference is made from U to V1.
            // UNDONE:
            // UNDONE: However, this causes an unfortunate interaction with what
            // UNDONE: looks like a bug in our implementation of section 15.2 of
            // UNDONE: the specification. Namely, it appears that the code which
            // UNDONE: checks whether a given method is compatible with
            // UNDONE: a delegate type assumes that if method type inference succeeds,
            // UNDONE: then the inferred types are compatible with the delegate types.
            // UNDONE: This is not necessarily so; the inferred types could be compatible
            // UNDONE: via a conversion other than reference or identity.
            // UNDONE:
            // UNDONE: We should take an action item to investigate this problem.
            // UNDONE: Until then, we will turn off the proposed lower bound nullable
            // UNDONE: inference.

            // if (LowerBoundNullableInference(pSource, pDest))
            // {
            //     return;
            // }

            if (LowerBoundTupleInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: Otherwise... many cases for constructed generic types.
            if (LowerBoundConstructedInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            if (LowerBoundFunctionPointerTypeInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * Otherwise, no inferences are made.
        }

        private bool LowerBoundTypeParameterInference(TypeWithAnnotations source, TypeWithAnnotations target)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: * If V is one of the unfixed Xi then U is added to the set of bounds
            // SPEC:   for Xi.
            if (IsUnfixedTypeParameter(target))
            {
                AddBound(source, _lowerBounds, target);
                return true;
            }
            return false;
        }

        private static TypeWithAnnotations GetMatchingElementType(ArrayTypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            // It might be an array of same rank.
            if (target.IsArray())
            {
                var arrayTarget = (ArrayTypeSymbol)target;
                if (!arrayTarget.HasSameShapeAs(source))
                {
                    return default;
                }
                return arrayTarget.ElementTypeWithAnnotations;
            }

            // Or it might be IEnum<T> and source is rank one.

            if (!source.IsSZArray)
            {
                return default;
            }

            // Arrays are specified as being convertible to IEnumerable<T>, ICollection<T> and
            // IList<T>; we also honor their convertibility to IReadOnlyCollection<T> and
            // IReadOnlyList<T>, and make inferences accordingly.

            if (!target.IsPossibleArrayGenericInterface())
            {
                return default;
            }

            return ((NamedTypeSymbol)target).TypeArgumentWithDefinitionUseSiteDiagnostics(0, ref useSiteInfo);
        }

        private bool LowerBoundArrayInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            // SPEC: * Otherwise, if U is an array type Ue[...] and V is either an array
            // SPEC:   type Ve[...] of the same rank, or if U is a one-dimensional array
            // SPEC:   type Ue[] and V is one of IEnumerable<Ve>, ICollection<Ve> or
            // SPEC:   IList<Ve> then
            // SPEC:   * if Ue is known to be a reference type then a lower-bound inference
            // SPEC:     from Ue to Ve is made.
            // SPEC:   * otherwise an exact inference from Ue to Ve is made.

            if (!source.IsArray())
            {
                return false;
            }

            var arraySource = (ArrayTypeSymbol)source;
            var elementSource = arraySource.ElementTypeWithAnnotations;
            var elementTarget = GetMatchingElementType(arraySource, target, ref useSiteInfo);
            if (!elementTarget.HasType)
            {
                return false;
            }

            if (elementSource.Type.IsReferenceType)
            {
                LowerBoundInference(elementSource, elementTarget, ref useSiteInfo);
            }
            else
            {
                ExactInference(elementSource, elementTarget, ref useSiteInfo);
            }

            return true;
        }

        private bool LowerBoundSpanInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source is not null);
            Debug.Assert(target is not null);

            if (IsFeatureFirstClassSpanEnabled && (
                // SPEC: * V is a Span<V1> and U is an array type U1[] or a Span<U1>
                (
                    target.IsSpan() &&
                    (source.IsSZArray() || source.IsSpan())
                ) ||
                // SPEC: * V is a ReadOnlySpan<V1> and U is an array type U1[] or a Span<U1> or ReadOnlySpan<U1>
                (
                    target.IsReadOnlySpan() &&
                    (source.IsSZArray() || source.IsSpan() || source.IsReadOnlySpan())
                )
            ))
            {
                var sourceElementType = GetSpanOrSZArrayElementType(source);
                var targetElementType = GetSpanElementType(target);

                // SPEC: * If U1 is not known to be a reference type then an exact inference is made
                // SPEC: * If V is a Span<V1>, then an exact inference is made
                if (!sourceElementType.Type.IsReferenceType || target.IsSpan())
                {
                    ExactInference(sourceElementType, targetElementType, ref useSiteInfo);
                }
                else
                {
                    LowerBoundInference(sourceElementType, targetElementType, ref useSiteInfo);
                }

                return true;
            }

            return false;
        }

        private bool LowerBoundNullableInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            return ExactOrBoundsNullableInference(ExactOrBoundsKind.LowerBound, source, target, ref useSiteInfo);
        }

        private bool LowerBoundConstructedInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            var constructedTarget = target as NamedTypeSymbol;
            if ((object)constructedTarget == null)
            {
                return false;
            }

            if (constructedTarget.AllTypeArgumentCount() == 0)
            {
                return false;
            }

            // SPEC: * Otherwise, if V is a constructed class or struct type C<V1...Vk> 
            // SPEC:   and U is C<U1...Uk> then an exact inference
            // SPEC:   is made from each Ui to the corresponding Vi.

            // SPEC: * Otherwise, if V is a constructed interface or delegate type C<V1...Vk> 
            // SPEC:   and U is C<U1...Uk> then an exact inference,
            // SPEC:   lower bound inference or upper bound inference
            // SPEC:   is made from each Ui to the corresponding Vi.

            var constructedSource = source as NamedTypeSymbol;
            if ((object)constructedSource != null &&
                TypeSymbol.Equals(constructedSource.OriginalDefinition, constructedTarget.OriginalDefinition, TypeCompareKind.ConsiderEverything2))
            {
                if (constructedSource.IsInterface || constructedSource.IsDelegateType())
                {
                    LowerBoundTypeArgumentInference(constructedSource, constructedTarget, ref useSiteInfo);
                }
                else
                {
                    ExactTypeArgumentInference(constructedSource, constructedTarget, ref useSiteInfo);
                }
                return true;
            }

            // SPEC: * Otherwise, if V is a class type C<V1...Vk> and U is a class type which
            // SPEC:   inherits directly or indirectly from C<U1...Uk> then an exact ...
            // SPEC: * ... and U is a type parameter with effective base class ...
            // SPEC: * ... and U is a type parameter with an effective base class which inherits ...

            if (LowerBoundClassInference(source, constructedTarget, ref useSiteInfo))
            {
                return true;
            }

            // SPEC: * Otherwise, if V is an interface type C<V1...Vk> and U is a class type
            // SPEC:   or struct type and there is a unique set U1...Uk such that U directly 
            // SPEC:   or indirectly implements C<U1...Uk> then an exact ...
            // SPEC: * ... and U is an interface type ...
            // SPEC: * ... and U is a type parameter ...

            if (LowerBoundInterfaceInference(source, constructedTarget, ref useSiteInfo))
            {
                return true;
            }

            return false;
        }

        private bool LowerBoundClassInference(TypeSymbol source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            if (target.TypeKind != TypeKind.Class)
            {
                return false;
            }

            // Spec: 7.5.2.9 Lower-bound interfaces 
            // SPEC: * Otherwise, if V is a class type C<V1...Vk> and U is a class type which
            // SPEC:   inherits directly or indirectly from C<U1...Uk> 
            // SPEC:   then an exact inference is made from each Ui to the corresponding Vi.
            // SPEC: * Otherwise, if V is a class type C<V1...Vk> and U is a type parameter
            // SPEC:   with effective base class C<U1...Uk> 
            // SPEC:   then an exact inference is made from each Ui to the corresponding Vi.
            // SPEC: * Otherwise, if V is a class type C<V1...Vk> and U is a type parameter
            // SPEC:   with an effective base class which inherits directly or indirectly from
            // SPEC:   C<U1...Uk> then an exact inference is made
            // SPEC:   from each Ui to the corresponding Vi.

            NamedTypeSymbol sourceBase = null;

            if (source.TypeKind == TypeKind.Class)
            {
                sourceBase = source.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
            }
            else if (source.TypeKind == TypeKind.TypeParameter)
            {
                sourceBase = ((TypeParameterSymbol)source).EffectiveBaseClass(ref useSiteInfo);
            }

            while ((object)sourceBase != null)
            {
                if (TypeSymbol.Equals(sourceBase.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything2))
                {
                    ExactTypeArgumentInference(sourceBase, target, ref useSiteInfo);
                    return true;
                }
                sourceBase = sourceBase.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
            }
            return false;
        }

        private bool LowerBoundInterfaceInference(TypeSymbol source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            if (!target.IsInterface)
            {
                return false;
            }

            // Spec 7.5.2.9 Lower-bound interfaces
            // SPEC: * Otherwise, if V [target] is an interface type C<V1...Vk> and U [source] is a class type
            // SPEC:   or struct type and there is a unique set U1...Uk such that U directly 
            // SPEC:   or indirectly implements C<U1...Uk> then an
            // SPEC:   exact, upper-bound, or lower-bound inference ...
            // SPEC: * ... and U is an interface type ...
            // SPEC: * ... and U is a type parameter ...

            ImmutableArray<NamedTypeSymbol> allInterfaces;
            switch (source.TypeKind)
            {
                case TypeKind.Struct:
                case TypeKind.Class:
                case TypeKind.Interface:
                    allInterfaces = source.AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
                    break;

                case TypeKind.TypeParameter:
                    var typeParameter = (TypeParameterSymbol)source;
                    allInterfaces = typeParameter.EffectiveBaseClass(ref useSiteInfo).
                                        AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo).
                                        Concat(typeParameter.AllEffectiveInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo));
                    break;

                default:
                    return false;
            }

            // duplicates with only nullability differences can be merged (to avoid an error in type inference)
            allInterfaces = ModuloReferenceTypeNullabilityDifferences(allInterfaces, VarianceKind.In);

            NamedTypeSymbol matchingInterface = GetInterfaceInferenceBound(allInterfaces, target);
            if ((object)matchingInterface == null)
            {
                return false;
            }
            LowerBoundTypeArgumentInference(matchingInterface, target, ref useSiteInfo);
            return true;
        }

        internal static ImmutableArray<NamedTypeSymbol> ModuloReferenceTypeNullabilityDifferences(ImmutableArray<NamedTypeSymbol> interfaces, VarianceKind variance)
        {
            var dictionary = PooledDictionaryIgnoringNullableModifiersForReferenceTypes.GetInstance();

            foreach (var @interface in interfaces)
            {
                if (dictionary.TryGetValue(@interface, out var found))
                {
                    var merged = (NamedTypeSymbol)found.MergeEquivalentTypes(@interface, variance);
                    dictionary[@interface] = merged;
                }
                else
                {
                    dictionary.Add(@interface, @interface);
                }
            }

            var result = dictionary.Count != interfaces.Length ? dictionary.Values.ToImmutableArray() : interfaces;
            dictionary.Free();
            return result;
        }

        private void LowerBoundTypeArgumentInference(NamedTypeSymbol source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: The choice of inference for the i-th type argument is made
            // SPEC: based on the declaration of the i-th type parameter of C, as
            // SPEC: follows:
            // SPEC: * if Ui is known to be of reference type and the i-th type parameter
            // SPEC:   was declared as covariant then a lower bound inference is made.
            // SPEC: * if Ui is known to be of reference type and the i-th type parameter
            // SPEC:   was declared as contravariant then an upper bound inference is made.
            // SPEC: * otherwise, an exact inference is made.

            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);
            Debug.Assert(TypeSymbol.Equals(source.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything2));

            var typeParameters = ArrayBuilder<TypeParameterSymbol>.GetInstance();
            var sourceTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
            var targetTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();

            source.OriginalDefinition.GetAllTypeParameters(typeParameters);
            source.GetAllTypeArguments(sourceTypeArguments, ref useSiteInfo);
            target.GetAllTypeArguments(targetTypeArguments, ref useSiteInfo);

            Debug.Assert(typeParameters.Count == sourceTypeArguments.Count);
            Debug.Assert(typeParameters.Count == targetTypeArguments.Count);

            for (int arg = 0; arg < sourceTypeArguments.Count; ++arg)
            {
                var typeParameter = typeParameters[arg];
                var sourceTypeArgument = sourceTypeArguments[arg];
                var targetTypeArgument = targetTypeArguments[arg];

                if (sourceTypeArgument.Type.IsReferenceType && typeParameter.Variance == VarianceKind.Out)
                {
                    LowerBoundInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
                else if (sourceTypeArgument.Type.IsReferenceType && typeParameter.Variance == VarianceKind.In)
                {
                    UpperBoundInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
                else
                {
                    ExactInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
            }

            typeParameters.Free();
            sourceTypeArguments.Free();
            targetTypeArguments.Free();
        }

#nullable enable
        private bool LowerBoundFunctionPointerTypeInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (source is not FunctionPointerTypeSymbol { Signature: { } sourceSignature } || target is not FunctionPointerTypeSymbol { Signature: { } targetSignature })
            {
                return false;
            }

            if (sourceSignature.ParameterCount != targetSignature.ParameterCount)
            {
                return false;
            }

            if (!FunctionPointerRefKindsEqual(sourceSignature, targetSignature) || !FunctionPointerCallingConventionsEqual(sourceSignature, targetSignature))
            {
                return false;
            }

            // Reference parameters are treated as "input" variance by default, and reference return types are treated as out variance by default.
            // If they have a ref kind or are not reference types, then they are treated as invariant.
            for (int i = 0; i < sourceSignature.ParameterCount; i++)
            {
                var sourceParam = sourceSignature.Parameters[i];
                var targetParam = targetSignature.Parameters[i];

                if ((sourceParam.Type.IsReferenceType || sourceParam.Type.IsFunctionPointer()) && sourceParam.RefKind == RefKind.None)
                {
                    UpperBoundInference(sourceParam.TypeWithAnnotations, targetParam.TypeWithAnnotations, ref useSiteInfo);
                }
                else
                {
                    ExactInference(sourceParam.TypeWithAnnotations, targetParam.TypeWithAnnotations, ref useSiteInfo);
                }
            }

            if ((sourceSignature.ReturnType.IsReferenceType || sourceSignature.ReturnType.IsFunctionPointer()) && sourceSignature.RefKind == RefKind.None)
            {
                LowerBoundInference(sourceSignature.ReturnTypeWithAnnotations, targetSignature.ReturnTypeWithAnnotations, ref useSiteInfo);
            }
            else
            {
                ExactInference(sourceSignature.ReturnTypeWithAnnotations, targetSignature.ReturnTypeWithAnnotations, ref useSiteInfo);
            }

            return true;
        }
#nullable disable

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Upper-bound inferences
        //
        private void UpperBoundInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: An upper-bound inference from a type U to a type V is made as follows:

            // SPEC: * Otherwise, if V is nullable type V1? and U is nullable type U1?
            // SPEC:   then an exact inference is made from U1 to V1.

            if (UpperBoundNullableInference(source, target, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * If V is one of the unfixed Xi then U is added to the set of 
            // SPEC:   upper bounds for Xi.

            if (UpperBoundTypeParameterInference(source, target))
            {
                return;
            }

            // SPEC: * Otherwise, if V is an array type Ve[...] and U is an array
            // SPEC:   type Ue[...] of the same rank, or if V is a one-dimensional array
            // SPEC:   type Ve[] and U is one of IEnumerable<Ue>, ICollection<Ue> or
            // SPEC:   IList<Ue> then
            // SPEC:   * if Ue is known to be a reference type then an upper-bound inference
            // SPEC:     from Ue to Ve is made.
            // SPEC:   * otherwise an exact inference from Ue to Ve is made.

            if (UpperBoundArrayInference(source, target, ref useSiteInfo))
            {
                return;
            }

            Debug.Assert(source.Type.IsReferenceType || source.Type.IsFunctionPointer());

            // NOTE: spec would ask us to do the following checks, but since the value types
            //       are trivially handled as exact inference in the callers, we do not have to.

            //if (ExactTupleInference(source, target, ref useSiteInfo))
            //{
            //    return;
            //}

            // SPEC: * Otherwise... cases for constructed types

            if (UpperBoundConstructedInference(source, target, ref useSiteInfo))
            {
                return;
            }

            if (UpperBoundFunctionPointerTypeInference(source.Type, target.Type, ref useSiteInfo))
            {
                return;
            }

            // SPEC: * Otherwise, no inferences are made.
        }

        private bool UpperBoundTypeParameterInference(TypeWithAnnotations source, TypeWithAnnotations target)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);
            // SPEC: * If V is one of the unfixed Xi then U is added to the set of upper bounds
            // SPEC:   for Xi.
            if (IsUnfixedTypeParameter(target))
            {
                AddBound(source, _upperBounds, target);
                return true;
            }
            return false;
        }

        private bool UpperBoundArrayInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(source.HasType);
            Debug.Assert(target.HasType);

            // SPEC: * Otherwise, if V is an array type Ve[...] and U is an array
            // SPEC:   type Ue[...] of the same rank, or if V is a one-dimensional array
            // SPEC:   type Ve[] and U is one of IEnumerable<Ue>, ICollection<Ue> or
            // SPEC:   IList<Ue> then
            // SPEC:   * if Ue is known to be a reference type then an upper-bound inference
            // SPEC:     from Ue to Ve is made.
            // SPEC:   * otherwise an exact inference from Ue to Ve is made.

            if (!target.Type.IsArray())
            {
                return false;
            }
            var arrayTarget = (ArrayTypeSymbol)target.Type;
            var elementTarget = arrayTarget.ElementTypeWithAnnotations;
            var elementSource = GetMatchingElementType(arrayTarget, source.Type, ref useSiteInfo);
            if (!elementSource.HasType)
            {
                return false;
            }

            if (elementSource.Type.IsReferenceType)
            {
                UpperBoundInference(elementSource, elementTarget, ref useSiteInfo);
            }
            else
            {
                ExactInference(elementSource, elementTarget, ref useSiteInfo);
            }

            return true;
        }

        private bool UpperBoundNullableInference(TypeWithAnnotations source, TypeWithAnnotations target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            return ExactOrBoundsNullableInference(ExactOrBoundsKind.UpperBound, source, target, ref useSiteInfo);
        }

        private bool UpperBoundConstructedInference(TypeWithAnnotations sourceWithAnnotations, TypeWithAnnotations targetWithAnnotations, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(sourceWithAnnotations.HasType);
            Debug.Assert(targetWithAnnotations.HasType);
            var source = sourceWithAnnotations.Type;
            var target = targetWithAnnotations.Type;

            var constructedSource = source as NamedTypeSymbol;
            if ((object)constructedSource == null)
            {
                return false;
            }

            if (constructedSource.AllTypeArgumentCount() == 0)
            {
                return false;
            }

            // SPEC: * Otherwise, if V is a constructed type C<V1...Vk> and U is
            // SPEC:   C<U1...Uk> then an exact inference,
            // SPEC:   lower bound inference or upper bound inference
            // SPEC:   is made from each Ui to the corresponding Vi.

            var constructedTarget = target as NamedTypeSymbol;

            if ((object)constructedTarget != null &&
                TypeSymbol.Equals(constructedSource.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything2))
            {
                if (constructedTarget.IsInterface || constructedTarget.IsDelegateType())
                {
                    UpperBoundTypeArgumentInference(constructedSource, constructedTarget, ref useSiteInfo);
                }
                else
                {
                    ExactTypeArgumentInference(constructedSource, constructedTarget, ref useSiteInfo);
                }
                return true;
            }

            // SPEC: * Otherwise, if U is a class type C<U1...Uk> and V is a class type which
            // SPEC:   inherits directly or indirectly from C<V1...Vk> then an exact ...

            if (UpperBoundClassInference(constructedSource, target, ref useSiteInfo))
            {
                return true;
            }

            // SPEC: * Otherwise, if U is an interface type C<U1...Uk> and V is a class type
            // SPEC:   or struct type and there is a unique set V1...Vk such that V directly 
            // SPEC:   or indirectly implements C<V1...Vk> then an exact ...
            // SPEC: * ... and U is an interface type ...

            if (UpperBoundInterfaceInference(constructedSource, target, ref useSiteInfo))
            {
                return true;
            }

            return false;
        }

        private bool UpperBoundClassInference(NamedTypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            if (source.TypeKind != TypeKind.Class || target.TypeKind != TypeKind.Class)
            {
                return false;
            }

            // SPEC: * Otherwise, if U is a class type C<U1...Uk> and V is a class type which
            // SPEC:   inherits directly or indirectly from C<V1...Vk> then an exact 
            // SPEC:   inference is made from each Ui to the corresponding Vi.

            var targetBase = target.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
            while ((object)targetBase != null)
            {
                if (TypeSymbol.Equals(targetBase.OriginalDefinition, source.OriginalDefinition, TypeCompareKind.ConsiderEverything2))
                {
                    ExactTypeArgumentInference(source, targetBase, ref useSiteInfo);
                    return true;
                }

                targetBase = targetBase.BaseTypeWithDefinitionUseSiteDiagnostics(ref useSiteInfo);
            }

            return false;
        }

        private bool UpperBoundInterfaceInference(NamedTypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);

            if (!source.IsInterface)
            {
                return false;
            }

            // SPEC: * Otherwise, if U [source] is an interface type C<U1...Uk> and V [target] is a class type
            // SPEC:   or struct type and there is a unique set V1...Vk such that V directly 
            // SPEC:   or indirectly implements C<V1...Vk> then an exact ...
            // SPEC: * ... and U is an interface type ...

            switch (target.TypeKind)
            {
                case TypeKind.Struct:
                case TypeKind.Class:
                case TypeKind.Interface:
                    break;

                default:
                    return false;
            }

            ImmutableArray<NamedTypeSymbol> allInterfaces = target.AllInterfacesWithDefinitionUseSiteDiagnostics(ref useSiteInfo);

            // duplicates with only nullability differences can be merged (to avoid an error in type inference)
            allInterfaces = ModuloReferenceTypeNullabilityDifferences(allInterfaces, VarianceKind.Out);

            NamedTypeSymbol bestInterface = GetInterfaceInferenceBound(allInterfaces, source);
            if ((object)bestInterface == null)
            {
                return false;
            }

            UpperBoundTypeArgumentInference(source, bestInterface, ref useSiteInfo);
            return true;
        }

        private void UpperBoundTypeArgumentInference(NamedTypeSymbol source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // SPEC: The choice of inference for the i-th type argument is made
            // SPEC: based on the declaration of the i-th type parameter of C, as
            // SPEC: follows:
            // SPEC: * if Ui is known to be of reference type and the i-th type parameter
            // SPEC:   was declared as covariant then an upper-bound inference is made.
            // SPEC: * if Ui is known to be of reference type and the i-th type parameter
            // SPEC:   was declared as contravariant then a lower-bound inference is made.
            // SPEC: * otherwise, an exact inference is made.

            Debug.Assert((object)source != null);
            Debug.Assert((object)target != null);
            Debug.Assert(TypeSymbol.Equals(source.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything2));

            var typeParameters = ArrayBuilder<TypeParameterSymbol>.GetInstance();
            var sourceTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
            var targetTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();

            source.OriginalDefinition.GetAllTypeParameters(typeParameters);
            source.GetAllTypeArguments(sourceTypeArguments, ref useSiteInfo);
            target.GetAllTypeArguments(targetTypeArguments, ref useSiteInfo);

            Debug.Assert(typeParameters.Count == sourceTypeArguments.Count);
            Debug.Assert(typeParameters.Count == targetTypeArguments.Count);

            for (int arg = 0; arg < sourceTypeArguments.Count; ++arg)
            {
                var typeParameter = typeParameters[arg];
                var sourceTypeArgument = sourceTypeArguments[arg];
                var targetTypeArgument = targetTypeArguments[arg];

                if (sourceTypeArgument.Type.IsReferenceType && typeParameter.Variance == VarianceKind.Out)
                {
                    UpperBoundInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
                else if (sourceTypeArgument.Type.IsReferenceType && typeParameter.Variance == VarianceKind.In)
                {
                    LowerBoundInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
                else
                {
                    ExactInference(sourceTypeArgument, targetTypeArgument, ref useSiteInfo);
                }
            }

            typeParameters.Free();
            sourceTypeArguments.Free();
            targetTypeArguments.Free();
        }

#nullable enable
        private bool UpperBoundFunctionPointerTypeInference(TypeSymbol source, TypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (source is not FunctionPointerTypeSymbol { Signature: { } sourceSignature } || target is not FunctionPointerTypeSymbol { Signature: { } targetSignature })
            {
                return false;
            }

            if (sourceSignature.ParameterCount != targetSignature.ParameterCount)
            {
                return false;
            }

            if (!FunctionPointerRefKindsEqual(sourceSignature, targetSignature) || !FunctionPointerCallingConventionsEqual(sourceSignature, targetSignature))
            {
                return false;
            }

            // Reference parameters are treated as "input" variance by default, and reference return types are treated as out variance by default.
            // If they have a ref kind or are not reference types, then they are treated as invariant.
            for (int i = 0; i < sourceSignature.ParameterCount; i++)
            {
                var sourceParam = sourceSignature.Parameters[i];
                var targetParam = targetSignature.Parameters[i];

                if ((sourceParam.Type.IsReferenceType || sourceParam.Type.IsFunctionPointer()) && sourceParam.RefKind == RefKind.None)
                {
                    LowerBoundInference(sourceParam.TypeWithAnnotations, targetParam.TypeWithAnnotations, ref useSiteInfo);
                }
                else
                {
                    ExactInference(sourceParam.TypeWithAnnotations, targetParam.TypeWithAnnotations, ref useSiteInfo);
                }
            }

            if ((sourceSignature.ReturnType.IsReferenceType || sourceSignature.ReturnType.IsFunctionPointer()) && sourceSignature.RefKind == RefKind.None)
            {
                UpperBoundInference(sourceSignature.ReturnTypeWithAnnotations, targetSignature.ReturnTypeWithAnnotations, ref useSiteInfo);
            }
            else
            {
                ExactInference(sourceSignature.ReturnTypeWithAnnotations, targetSignature.ReturnTypeWithAnnotations, ref useSiteInfo);
            }

            return true;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Fixing
        //
        private bool Fix(int iParam, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(IsUnfixed(iParam));

            var typeParameter = _methodTypeParameters[iParam];
            var exact = _exactBounds[iParam];
            var lower = _lowerBounds[iParam];
            var upper = _upperBounds[iParam];

            var best = Fix(_compilation, _conversions, typeParameter, exact, lower, upper, ref useSiteInfo);
            if (!best.Type.HasType)
            {
                return false;
            }

#if DEBUG
            if (_conversions.IncludeNullability)
            {
                // If the first attempt succeeded, the result should be the same as
                // the second attempt, although perhaps with different nullability.
                var discardedUseSiteInfo = CompoundUseSiteInfo<AssemblySymbol>.Discarded;
                var withoutNullability = Fix(_compilation, _conversions.WithNullability(false), typeParameter, exact, lower, upper, ref discardedUseSiteInfo).Type;
                // https://github.com/dotnet/roslyn/issues/27961 Results may differ by tuple names or dynamic.
                // See NullableReferenceTypesTests.TypeInference_TupleNameDifferences_01 for example.
                Debug.Assert(best.Type.Type.Equals(withoutNullability.Type, TypeCompareKind.IgnoreDynamicAndTupleNames | TypeCompareKind.IgnoreNullableModifiersForReferenceTypes));
            }
#endif

            _fixedResults[iParam] = best;
            UpdateDependenciesAfterFix(iParam);
            return true;
        }

        private static (TypeWithAnnotations Type, bool FromFunctionType) Fix(
            CSharpCompilation compilation,
            ConversionsBase conversions,
            TypeParameterSymbol typeParameter,
            HashSet<TypeWithAnnotations>? exact,
            HashSet<TypeWithAnnotations>? lower,
            HashSet<TypeWithAnnotations>? upper,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            // UNDONE: This method makes a lot of garbage.

            // SPEC: An unfixed type parameter with a set of bounds is fixed as follows:

            // SPEC: * The set of candidate types starts out as the set of all types in
            // SPEC:   the bounds.

            // SPEC: * We then examine each bound in turn. For each exact bound U of Xi,
            // SPEC:   all types which are not identical to U are removed from the candidate set.

            // Optimization: if we have two or more exact bounds, fixing is impossible.

            var candidates = new Dictionary<TypeWithAnnotations, TypeWithAnnotations>(EqualsIgnoringDynamicTupleNamesAndNullabilityComparer.Instance);

            Debug.Assert(!containsFunctionTypes(exact));
            Debug.Assert(!containsFunctionTypes(upper));

            Predicate<TypeWithAnnotations> lowerPredicate;
            // Function types are dropped if there are any non-function types.
            if (containsFunctionTypes(lower) &&
                (containsNonFunctionTypes(lower) || containsNonFunctionTypes(exact) || containsNonFunctionTypes(upper)))
            {
                lowerPredicate = static type => !isFunctionType(type, out _);
            }
            else
            {
                // Remove any function types with no delegate type.
                lowerPredicate = static type => !isFunctionType(type, out var functionType) || functionType.GetInternalDelegateType() is not null;
            }

            // Optimization: if we have one exact bound then we need not add any
            // inexact bounds; we're just going to remove them anyway.

            if (exact == null)
            {
                if (lower != null)
                {
                    // Lower bounds represent co-variance.
                    AddAllCandidates(candidates, lower, lowerPredicate, VarianceKind.Out, conversions);
                }
                if (upper != null)
                {
                    // Upper bounds represent contra-variance.
                    AddAllCandidates(candidates, upper, predicate: null, VarianceKind.In, conversions);
                }
            }
            else
            {
                // Exact bounds represent invariance.
                AddAllCandidates(candidates, exact, predicate: null, VarianceKind.None, conversions);
                if (candidates.Count >= 2)
                {
                    return default;
                }
            }

            if (candidates.Count == 0)
            {
                return default;
            }

            // Don't mutate the collection as we're iterating it.
            var initialCandidates = ArrayBuilder<TypeWithAnnotations>.GetInstance();
            GetAllCandidates(candidates, initialCandidates);

            // SPEC:   For each lower bound U of Xi all types to which there is not an
            // SPEC:   implicit conversion from U are removed from the candidate set.

            if (lower != null)
            {
                MergeOrRemoveCandidates(candidates, lower, lowerPredicate, initialCandidates, conversions, VarianceKind.Out, ref useSiteInfo);
            }

            // SPEC:   For each upper bound U of Xi all types from which there is not an
            // SPEC:   implicit conversion to U are removed from the candidate set.

            if (upper != null)
            {
                MergeOrRemoveCandidates(candidates, upper, predicate: null, initialCandidates, conversions, VarianceKind.In, ref useSiteInfo);
            }

            initialCandidates.Clear();
            GetAllCandidates(candidates, initialCandidates);

            // SPEC: * If among the remaining candidate types there is a unique type V to
            // SPEC:   which there is an implicit conversion from all the other candidate
            // SPEC:   types, then the parameter is fixed to V.
            TypeWithAnnotations best = default;
            foreach (var candidate in initialCandidates)
            {
                foreach (var candidate2 in initialCandidates)
                {
                    if (!candidate.Equals(candidate2, TypeCompareKind.ConsiderEverything) &&
                        !ImplicitConversionExists(candidate2, candidate, ref useSiteInfo, conversions.WithNullability(false)))
                    {
                        goto OuterBreak;
                    }
                }

                if (!best.HasType)
                {
                    best = candidate;
                }
                else
                {
                    Debug.Assert(!best.Equals(candidate, TypeCompareKind.IgnoreDynamicAndTupleNames | TypeCompareKind.IgnoreNullableModifiersForReferenceTypes));
                    // best candidate is not unique
                    best = default;
                    break;
                }

OuterBreak:
                ;
            }

            initialCandidates.Free();

            bool fromFunctionType = false;
            if (isFunctionType(best, out var functionType))
            {
                // Realize the type as TDelegate, or Expression<TDelegate> if the type parameter
                // is constrained to System.Linq.Expressions.Expression.
                var resultType = functionType.GetInternalDelegateType();
                if (hasExpressionTypeConstraint(typeParameter))
                {
                    var expressionOfTType = compilation.GetWellKnownType(WellKnownType.System_Linq_Expressions_Expression_T);
                    resultType = expressionOfTType.Construct(resultType);
                }
                best = TypeWithAnnotations.Create(resultType, best.NullableAnnotation);
                fromFunctionType = true;
            }

            return (best, fromFunctionType);

            static bool containsFunctionTypes([NotNullWhen(true)] HashSet<TypeWithAnnotations>? types)
            {
                return types?.Any(t => isFunctionType(t, out _)) == true;
            }

            static bool containsNonFunctionTypes([NotNullWhen(true)] HashSet<TypeWithAnnotations>? types)
            {
                return types?.Any(t => !isFunctionType(t, out _)) == true;
            }

            static bool isFunctionType(TypeWithAnnotations type, [NotNullWhen(true)] out FunctionTypeSymbol? functionType)
            {
                functionType = type.Type as FunctionTypeSymbol;
                return functionType is not null;
            }

            static bool hasExpressionTypeConstraint(TypeParameterSymbol typeParameter)
            {
                var constraintTypes = typeParameter.ConstraintTypesNoUseSiteDiagnostics;
                return constraintTypes.Any(static t => isExpressionType(t.Type));
            }

            static bool isExpressionType(TypeSymbol? type)
            {
                while (type is { })
                {
                    if (type.IsGenericOrNonGenericExpressionType(out _))
                    {
                        return true;
                    }
                    type = type.BaseTypeNoUseSiteDiagnostics;
                }
                return false;
            }
        }

        private static bool ImplicitConversionExists(TypeWithAnnotations sourceWithAnnotations, TypeWithAnnotations destinationWithAnnotations, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo, ConversionsBase conversions)
        {
            var source = sourceWithAnnotations.Type;
            var destination = destinationWithAnnotations.Type;

            // SPEC VIOLATION: For the purpose of algorithm in Fix method, dynamic type is not considered convertible to any other type, including object.
            if (source.IsDynamic() && !destination.IsDynamic())
            {
                return false;
            }

            if (!conversions.HasTopLevelNullabilityImplicitConversion(sourceWithAnnotations, destinationWithAnnotations))
            {
                return false;
            }

            return conversions.ClassifyImplicitConversionFromTypeWhenNeitherOrBothFunctionTypes(source, destination, ref useSiteInfo).Exists;
        }
#nullable disable

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Inferred return type
        //
        private TypeWithAnnotations InferReturnType(BoundExpression source, NamedTypeSymbol target, ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert((object)target != null);
            Debug.Assert(target.IsDelegateType());
            Debug.Assert((object)target.DelegateInvokeMethod != null && !target.DelegateInvokeMethod.HasUseSiteError,
                         "This method should only be called for legal delegate types.");
            Debug.Assert(!target.DelegateInvokeMethod.ReturnsVoid);

            // We should not be computing the inferred return type unless we are converting
            // to a delegate type where all the input types are fixed.
            Debug.Assert(!HasUnfixedParamInInputType(source, target));

            // Spec 7.5.2.12: Inferred return type:
            // The inferred return type of an anonymous function F is used during
            // type inference and overload resolution. The inferred return type
            // can only be determined for an anonymous function where all parameter
            // types are known, either because they are explicitly given, provided
            // through an anonymous function conversion, or inferred during type
            // inference on an enclosing generic method invocation.
            // The inferred return type is determined as follows:
            // * If the body of F is an expression (that has a type) then the 
            //   inferred return type of F is the type of that expression.
            // * If the body of F is a block and the set of expressions in the
            //   blocks return statements has a best common type T then the
            //   inferred return type of F is T.
            // * Otherwise, a return type cannot be inferred for F.

            if (source.Kind != BoundKind.UnboundLambda)
            {
                return default;
            }

            var anonymousFunction = (UnboundLambda)source;
            if (anonymousFunction.HasSignature)
            {
                // Optimization: 
                // We know that the anonymous function has a parameter list. If it does not
                // have the same arity as the delegate, then it cannot possibly be applicable.
                // Rather than have type inference fail, we will simply not make a return
                // type inference and have type inference continue on.  Either inference
                // will fail, or we will infer a nonapplicable method. Either way, there
                // is no change to the semantics of overload resolution.

                var originalDelegateParameters = target.DelegateParameters();
                if (originalDelegateParameters.IsDefault)
                {
                    return default;
                }

                if (originalDelegateParameters.Length != anonymousFunction.ParameterCount)
                {
                    return default;
                }
            }

            var fixedDelegate = (NamedTypeSymbol)GetFixedDelegateOrFunctionPointer(target);
            var fixedDelegateParameters = fixedDelegate.DelegateParameters();
            // Optimization:
            // Similarly, if we have an entirely fixed delegate and an explicitly typed
            // anonymous function, then the parameter types had better be identical.
            // If not, applicability will eventually fail, so there is no semantic
            // difference caused by failing to make a return type inference.
            if (anonymousFunction.HasExplicitlyTypedParameterList)
            {
                for (int p = 0; p < anonymousFunction.ParameterCount; ++p)
                {
                    if (!anonymousFunction.ParameterType(p).Equals(fixedDelegateParameters[p].Type, TypeCompareKind.IgnoreDynamicAndTupleNames | TypeCompareKind.IgnoreNullableModifiersForReferenceTypes))
                    {
                        return default;
                    }
                }
            }

            // Future optimization: We could return default if the delegate has out or ref parameters
            // and the anonymous function is an implicitly typed lambda. It will not be applicable.

            // We have an entirely fixed delegate parameter list, which is of the same arity as
            // the anonymous function parameter list, and possibly exactly the same types if
            // the anonymous function is explicitly typed.  Make an inference from the
            // delegate parameters to the return type.

            var returnType = anonymousFunction.InferReturnType(_conversions, fixedDelegate, ref useSiteInfo, out bool inferredFromFunctionType);
            if (inferredFromFunctionType)
            {
                return default;
            }
            return returnType;
        }

        /// <summary>
        /// Return the interface with an original definition matches
        /// the original definition of the target. If the are no matches,
        /// or multiple matches, the return value is null.
        /// </summary>
        private static NamedTypeSymbol GetInterfaceInferenceBound(ImmutableArray<NamedTypeSymbol> interfaces, NamedTypeSymbol target)
        {
            Debug.Assert(target.IsInterface);
            NamedTypeSymbol matchingInterface = null;
            foreach (var currentInterface in interfaces)
            {
                if (TypeSymbol.Equals(currentInterface.OriginalDefinition, target.OriginalDefinition, TypeCompareKind.ConsiderEverything))
                {
                    if ((object)matchingInterface == null)
                    {
                        matchingInterface = currentInterface;
                    }
                    else if (!TypeSymbol.Equals(matchingInterface, currentInterface, TypeCompareKind.ConsiderEverything))
                    {
                        // Not unique. Bail out.
                        return null;
                    }
                }
            }
            return matchingInterface;
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // Helper methods
        //

        /// <summary>
        /// We apply type inference to an extension type, using the receiver as argument against the
        /// extension parameter.
        /// This lets us infer the type arguments of the extension type given this receiver.
        /// </summary>
        public static ImmutableArray<TypeWithAnnotations> InferTypeArgumentsFromReceiverType(
            NamedTypeSymbol extension,
            BoundExpression receiver,
            CSharpCompilation compilation,
            ConversionsBase conversions,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(extension is not null);
            Debug.Assert(extension.Arity > 0);
            Debug.Assert(extension.ExtensionParameter is not null);
            Debug.Assert(!extension.ExtensionParameter.Type.IsDynamic());
            Debug.Assert(extension.IsDefinition);
            Debug.Assert(receiver is not null);

            var inferrer = new MethodTypeInferrer(
                compilation,
                conversions,
                extension.TypeParameters,
                extension.ContainingType,
                [extension.ExtensionParameter.TypeWithAnnotations],
                [extension.ExtensionParameter.RefKind],
                [receiver],
                extensions: null,
                ordinals: null);

            if (!inferrer.InferTypeArgumentsFromFirstArgument(ref useSiteInfo))
            {
                return default;
            }

            return inferrer.GetInferredTypeArguments(out _);
        }

        ////////////////////////////////////////////////////////////////////////////////
        //
        // In error recovery and reporting scenarios we sometimes end up in a situation
        // like this:
        //
        // x.Goo( y=>
        //
        // and the question is, "is Goo a valid extension method of x?"  If Goo is
        // generic, then Goo will be something like:
        //
        // static Blah Goo<T>(this Bar<T> bar, Func<T, T> f){ ... }
        //
        // What we would like to know is: given _only_ the expression x, can we infer
        // what T is in Bar<T> ?  If we can, then for error recovery and reporting
        // we can provisionally consider Goo to be an extension method of x. If we 
        // cannot deduce this just from x then we should consider Goo to not be an
        // extension method of x, at least until we have more information.
        //
        // Clearly it is pointless to run multiple phases
        public static ImmutableArray<TypeWithAnnotations> InferTypeArgumentsFromFirstArgument(
            CSharpCompilation compilation,
            ConversionsBase conversions,
            MethodSymbol method,
            ImmutableArray<BoundExpression> arguments,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            if (!CanInferTypeArgumentsFromFirstArgument(compilation, conversions, method, arguments, ref useSiteInfo, out var inferrer))
            {
                return default;
            }

            return inferrer.GetInferredTypeArguments(out _);
        }

        public static bool CanInferTypeArgumentsFromFirstArgument(
            CSharpCompilation compilation,
            ConversionsBase conversions,
            MethodSymbol method,
            ImmutableArray<BoundExpression> arguments,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo,
            out MethodTypeInferrer inferrer)
        {
            Debug.Assert((object)method != null);
            Debug.Assert(method.Arity > 0);
            Debug.Assert(!arguments.IsDefault);

            // We need at least one formal parameter type and at least one argument.
            if ((method.ParameterCount < 1) || (arguments.Length < 1))
            {
                inferrer = default;
                return false;
            }

            Debug.Assert(!method.GetParameterType(0).IsDynamic());

            var constructedFromMethod = method.ConstructedFrom;

            inferrer = new MethodTypeInferrer(
                compilation,
                conversions,
                constructedFromMethod.TypeParameters,
                constructedFromMethod.ContainingType,
                constructedFromMethod.GetParameterTypes(),
                constructedFromMethod.ParameterRefKinds,
                arguments,
                extensions: null,
                ordinals: null);

            if (!inferrer.InferTypeArgumentsFromFirstArgument(ref useSiteInfo))
            {
                return false;
            }

            return true;
        }

        ////////////////////////////////////////////////////////////////////////////////

        private bool InferTypeArgumentsFromFirstArgument(ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(!_formalParameterTypes.IsDefault);
            Debug.Assert(_formalParameterTypes.Length >= 1);
            Debug.Assert(!_arguments.IsDefault);
            Debug.Assert(_arguments.Length >= 1);
            var dest = _formalParameterTypes[0];
            var argument = _arguments[0];
            TypeSymbol source = argument.Type;
            // Rule out lambdas, nulls, and so on.
            if (!IsReallyAType(source))
            {
                return false;
            }
            LowerBoundInference(_extensions.GetTypeWithAnnotations(argument), dest, ref useSiteInfo);
            // Now check to see that every type parameter used by the first
            // formal parameter type was successfully inferred.
            for (int iParam = 0; iParam < _methodTypeParameters.Length; ++iParam)
            {
                TypeParameterSymbol pParam = _methodTypeParameters[iParam];
                if (!dest.Type.ContainsTypeParameter(pParam))
                {
                    continue;
                }
                Debug.Assert(IsUnfixed(iParam));
                if (!HasBound(iParam) || !Fix(iParam, ref useSiteInfo))
                {
                    return false;
                }
            }
            return true;
        }

#nullable enable
        /// <summary>
        /// Return the inferred type arguments using null
        /// for any type arguments that were not inferred.
        /// </summary>
        private ImmutableArray<TypeWithAnnotations> GetInferredTypeArguments(out bool inferredFromFunctionType)
        {
            var builder = ArrayBuilder<TypeWithAnnotations>.GetInstance(_fixedResults.Length);
            inferredFromFunctionType = false;
            foreach (var fixedResult in _fixedResults)
            {
                builder.Add(fixedResult.Type);
                if (fixedResult.FromFunctionType)
                {
                    inferredFromFunctionType = true;
                }
            }
            return builder.ToImmutableAndFree();
        }

        private static bool IsReallyAType(TypeSymbol? type)
        {
            return type is { } &&
                !type.IsErrorType() &&
                !type.IsVoidType();
        }

        private static void GetAllCandidates(Dictionary<TypeWithAnnotations, TypeWithAnnotations> candidates, ArrayBuilder<TypeWithAnnotations> builder)
        {
            // Not using builder.AddRange here because the dictionary values enumerator is a struct, and calling AddRange will have to box the enumerator.
            // Instead, we increase the builder capacity, then loop over the values.
            // Also, we don't access candidates.Values to avoid realizing the Dictionary's internal ValueCollection (it's created lazily when Values is accessed)
            builder.EnsureCapacity(builder.Count + candidates.Count);
            foreach (var (_, value) in candidates)
            {
                builder.Add(value);
            }
        }

        private static void AddAllCandidates(
            Dictionary<TypeWithAnnotations, TypeWithAnnotations> candidates,
            HashSet<TypeWithAnnotations> bounds,
            Predicate<TypeWithAnnotations>? predicate,
            VarianceKind variance,
            ConversionsBase conversions)
        {
            foreach (var candidate in bounds)
            {
                if (predicate is not null && !predicate(candidate))
                {
                    continue;
                }

                var type = candidate;
                if (!conversions.IncludeNullability)
                {
                    // https://github.com/dotnet/roslyn/issues/30534: Should preserve
                    // distinct "not computed" state from initial binding.
                    type = type.SetUnknownNullabilityForReferenceTypes();
                }

                Debug.Assert(conversions.IncludeNullability ||
                    type.SetUnknownNullabilityForReferenceTypes().Equals(type, TypeCompareKind.ConsiderEverything));

                AddOrMergeCandidate(candidates, type, variance);
            }
        }

        private static void AddOrMergeCandidate(
            Dictionary<TypeWithAnnotations, TypeWithAnnotations> candidates,
            TypeWithAnnotations newCandidate,
            VarianceKind variance)
        {
            if (candidates.TryGetValue(newCandidate, out TypeWithAnnotations oldCandidate))
            {
                MergeAndReplaceIfStillCandidate(candidates, oldCandidate, newCandidate, variance);
            }
            else
            {
                candidates.Add(newCandidate, newCandidate);
            }
        }

        private static void MergeOrRemoveCandidates(
            Dictionary<TypeWithAnnotations, TypeWithAnnotations> candidates,
            HashSet<TypeWithAnnotations> bounds,
            Predicate<TypeWithAnnotations>? predicate,
            ArrayBuilder<TypeWithAnnotations> initialCandidates,
            ConversionsBase conversions,
            VarianceKind variance,
            ref CompoundUseSiteInfo<AssemblySymbol> useSiteInfo)
        {
            Debug.Assert(variance == VarianceKind.In || variance == VarianceKind.Out);
            // SPEC:   For each lower (upper) bound U of Xi all types to which there is not an
            // SPEC:   implicit conversion from (to) U are removed from the candidate set.
            var comparison = conversions.IncludeNullability ? TypeCompareKind.ConsiderEverything : TypeCompareKind.IgnoreNullableModifiersForReferenceTypes;
            foreach (var bound in bounds)
            {
                if (predicate is not null && !predicate(bound))
                {
                    continue;
                }

                foreach (var candidate in initialCandidates)
                {
                    if (bound.Equals(candidate, comparison))
                    {
                        continue;
                    }
                    TypeWithAnnotations source;
                    TypeWithAnnotations destination;
                    if (variance == VarianceKind.Out)
                    {
                        source = bound;
                        destination = candidate;
                    }
                    else
                    {
                        source = candidate;
                        destination = bound;
                    }
                    if (!ImplicitConversionExists(source, destination, ref useSiteInfo, conversions.WithNullability(false)))
                    {
                        candidates.Remove(candidate);
                        if (conversions.IncludeNullability && candidates.TryGetValue(bound, out var oldBound))
                        {
                            // merge the nullability from candidate into bound
                            var oldAnnotation = oldBound.NullableAnnotation;
                            var newAnnotation = oldAnnotation.MergeNullableAnnotation(candidate.NullableAnnotation, variance);
                            if (oldAnnotation != newAnnotation)
                            {
                                var newBound = TypeWithAnnotations.Create(oldBound.Type, newAnnotation);
                                candidates[bound] = newBound;
                            }
                        }
                    }
                    else if (bound.Equals(candidate, TypeCompareKind.IgnoreDynamicAndTupleNames | TypeCompareKind.IgnoreNullableModifiersForReferenceTypes))
                    {
                        // SPEC: 4.7 The Dynamic Type
                        //       Type inference (7.5.2) will prefer dynamic over object if both are candidates.
                        //
                        // This rule doesn't have to be implemented explicitly due to special handling of 
                        // conversions from dynamic in ImplicitConversionExists helper.
                        // 
                        MergeAndReplaceIfStillCandidate(candidates, candidate, bound, variance);
                    }
                }
            }
        }

        private static void MergeAndReplaceIfStillCandidate(
            Dictionary<TypeWithAnnotations, TypeWithAnnotations> candidates,
            TypeWithAnnotations oldCandidate,
            TypeWithAnnotations newCandidate,
            VarianceKind variance)
        {
            // We make an exception when new candidate is dynamic, for backwards compatibility 
            if (newCandidate.Type.IsDynamic())
            {
                return;
            }

            if (candidates.TryGetValue(oldCandidate, out TypeWithAnnotations latest))
            {
                // Note: we're ignoring the variance used merging previous candidates into `latest`.
                // If that variance is different than `variance`, we might infer the wrong nullability, but in that case,
                // we assume we'll report a warning when converting the arguments to the inferred parameter types.
                // (For instance, with F<T>(T x, T y, IIn<T> z) and interface IIn<in T> and interface IIOut<out T>, the
                // call F(IOut<object?>, IOut<object!>, IIn<IOut<object!>>) should find a nullability mismatch. Instead,
                // we'll merge the lower bounds IOut<object?> with IOut<object!> (using VarianceKind.Out) to produce
                // IOut<object?>, then merge that result with upper bound IOut<object!> (using VarianceKind.In)
                // to produce IOut<object?>. But then conversion of argument IIn<IOut<object!>> to parameter
                // IIn<IOut<object?>> will generate a warning at that point.)
                TypeWithAnnotations merged = latest.MergeEquivalentTypes(newCandidate, variance);
                candidates[oldCandidate] = merged;
            }
        }

        /// <summary>
        /// This is a comparer that ignores differences in dynamic-ness and tuple names.
        /// But it has a special case for top-level object vs. dynamic for purpose of method type inference.
        /// </summary>
        private sealed class EqualsIgnoringDynamicTupleNamesAndNullabilityComparer : EqualityComparer<TypeWithAnnotations>
        {
            internal static readonly EqualsIgnoringDynamicTupleNamesAndNullabilityComparer Instance = new EqualsIgnoringDynamicTupleNamesAndNullabilityComparer();

            public override int GetHashCode(TypeWithAnnotations obj)
            {
                return obj.Type.GetHashCode();
            }

            public override bool Equals(TypeWithAnnotations x, TypeWithAnnotations y)
            {
                // We do a equality test ignoring dynamic and tuple names differences,
                // but dynamic and object are not considered equal for backwards compatibility.
                if (x.Type.IsDynamic() ^ y.Type.IsDynamic()) { return false; }

                return x.Equals(y, TypeCompareKind.IgnoreDynamicAndTupleNames | TypeCompareKind.IgnoreNullableModifiersForReferenceTypes);
            }
        }
    }
}

