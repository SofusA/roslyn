﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.ImplementAbstractClass;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.Test.Utilities;
using Roslyn.Test.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.ImplementAbstractClass;

[Trait(Traits.Feature, Traits.Features.CodeActionsImplementAbstractClass)]
public sealed class ImplementAbstractClassTests_ThroughMemberTests(ITestOutputHelper logger)
    : AbstractCSharpDiagnosticProviderBasedUserDiagnosticTest_NoEditor(logger)
{
    internal override (DiagnosticAnalyzer?, CodeFixProvider) CreateDiagnosticProviderAndFixer(Workspace workspace)
        => (null, new CSharpImplementAbstractClassCodeFixProvider());

    private OptionsCollection AllOptionsOff
        => new OptionsCollection(GetLanguage())
        {
             { CSharpCodeStyleOptions.PreferExpressionBodiedMethods, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
             { CSharpCodeStyleOptions.PreferExpressionBodiedConstructors, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
             { CSharpCodeStyleOptions.PreferExpressionBodiedOperators, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
             { CSharpCodeStyleOptions.PreferExpressionBodiedAccessors, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
             { CSharpCodeStyleOptions.PreferExpressionBodiedProperties, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
             { CSharpCodeStyleOptions.PreferExpressionBodiedIndexers, CSharpCodeStyleOptions.NeverWithSilentEnforcement },
        };

    internal Task TestAllOptionsOffAsync(
        string initialMarkup,
        string expectedMarkup,
        OptionsCollection? options = null,
        ParseOptions? parseOptions = null)
    {
        options ??= new OptionsCollection(GetLanguage());
        options.AddRange(AllOptionsOff);

        return TestInRegularAndScriptAsync(
            initialMarkup,
            expectedMarkup,
            index: 1,
            new(options: options, parseOptions: parseOptions));
    }

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task FieldInBaseClassIsNotSuggested()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public Base Inner;

                public abstract void Method();
            }

            class [|Derived|] : Base
            {
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task FieldInMiddleClassIsNotSuggested()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            abstract class Middle : Base
            {
                public Base Inner;
            }

            class [|Derived|] : Base
            {
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task FieldOfSameDerivedTypeIsSuggested()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class [|Derived|] : Base
            {
                Derived inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class Derived : Base
            {
                Derived inner;

                public override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact]
    public Task RefParameters_Method()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method(int a, ref int b, in int c, ref readonly int d, out int e);
            }

            class [|Derived|] : Base
            {
                Derived inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method(int a, ref int b, in int c, ref readonly int d, out int e);
            }

            class Derived : Base
            {
                Derived inner;

                public override void Method(int a, ref int b, in int c, ref readonly int d, out int e)
                {
                    inner.Method(a, ref b, c, in d, out e);
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact]
    public Task RefParameters_Indexer()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int this[int a, in int b, ref readonly int c, out int d] { get; }
            }

            class [|Derived|] : Base
            {
                Derived inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int this[int a, in int b, ref readonly int c, out int d] { get; }
            }

            class Derived : Base
            {
                Derived inner;

                public override int this[int a, in int b, ref readonly int c, out int d] => inner[a, b, in c, out d];
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task SkipInaccessibleMember()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method1();
                protected abstract void Method2();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method1();
                protected abstract void Method2();
            }

            class {|Conflict:Derived|} : Base
            {
                Base inner;

                public override void Method1()
                {
                    inner.Method1();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task TestNotOfferedWhenOnlyUnimplementedMemberIsInaccessible()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract void Method1();
                protected abstract void Method2();
            }

            class [|Derived|] : Base
            {
                Base inner;

                public override void Method1()
                {
                    inner.Method1();
                }
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task FieldOfMoreSpecificTypeIsSuggested()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class [|Derived|] : Base
            {
                DerivedAgain inner;
            }

            class DerivedAgain : Derived
            {
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class Derived : Base
            {
                DerivedAgain inner;

                public override void Method()
                {
                    inner.Method();
                }
            }

            class DerivedAgain : Derived
            {
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task FieldOfConstrainedGenericTypeIsSuggested()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class [|Derived|]<T> : Base where T : Base
            {
                T inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class Derived<T> : Base where T : Base
            {
                T inner;

                public override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task DistinguishableOptionsAreShownForExplicitPropertyWithSameName()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            interface IInterface
            {
                Inner { get; }
            }

            class [|Derived|] : Base, IInterface
            {
                Base Inner { get; }

                Base IInterface.Inner { get; }
            }
            """,
            [
                AnalyzersResources.Implement_abstract_class,
                string.Format(AnalyzersResources.Implement_through_0, "Inner"),
                string.Format(AnalyzersResources.Implement_through_0, "IInterface.Inner"),
            ]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task NotOfferedForDynamicFields()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class [|Derived|] : Base
            {
                dynamic inner;
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task OfferedForStaticFields()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class [|Derived|] : Base
            {
                static Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method();
            }

            class Derived : Base
            {
                static Base inner;

                public override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PropertyIsDelegated()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int Property { get; set; }
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int Property { get; set; }
            }

            class Derived : Base
            {
                Base inner;

                public override int Property { get => inner.Property; set => inner.Property = value; }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PropertyIsDelegated_AllOptionsOff()
        => TestAllOptionsOffAsync(
            """
            abstract class Base
            {
                public abstract int Property { get; set; }
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int Property { get; set; }
            }

            class Derived : Base
            {
                Base inner;

                public override int Property
                {
                    get
                    {
                        return inner.Property;
                    }

                    set
                    {
                        inner.Property = value;
                    }
                }
            }
            """);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PropertyWithSingleAccessorIsDelegated()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int GetOnly { get; }
                public abstract int SetOnly { set; }
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int GetOnly { get; }
                public abstract int SetOnly { set; }
            }

            class Derived : Base
            {
                Base inner;

                public override int GetOnly => inner.GetOnly;

                public override int SetOnly { set => inner.SetOnly = value; }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PropertyWithSingleAccessorIsDelegated_AllOptionsOff()
        => TestAllOptionsOffAsync(
            """
            abstract class Base
            {
                public abstract int GetOnly { get; }
                public abstract int SetOnly { set; }
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int GetOnly { get; }
                public abstract int SetOnly { set; }
            }

            class Derived : Base
            {
                Base inner;

                public override int GetOnly
                {
                    get
                    {
                        return inner.GetOnly;
                    }
                }

                public override int SetOnly
                {
                    set
                    {
                        inner.SetOnly = value;
                    }
                }
            }
            """);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task EventIsDelegated()
        => TestInRegularAndScriptAsync(
            """
            using System;

            abstract class Base
            {
                public abstract event Action Event;
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            using System;

            abstract class Base
            {
                public abstract event Action Event;
            }

            class Derived : Base
            {
                Base inner;

                public override event Action Event
                {
                    add
                    {
                        inner.Event += value;
                    }

                    remove
                    {
                        inner.Event -= value;
                    }
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task OnlyOverridableMethodsAreOverridden()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract void Method();

                public void NonVirtualMethod();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract void Method();

                public void NonVirtualMethod();
            }

            class Derived : Base
            {
                Base inner;

                public override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task ProtectedMethodsCannotBeDelegatedThroughBaseType()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                protected abstract void Method();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task ProtectedMethodsCanBeDelegatedThroughSameType()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                protected abstract void Method();
            }

            class [|Derived|] : Base
            {
                Derived inner;
            }
            """,
            """
            abstract class Base
            {
                protected abstract void Method();
            }

            class Derived : Base
            {
                Derived inner;

                protected override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task ProtectedInternalMethodsAreOverridden()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                protected internal abstract void Method();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                protected internal abstract void Method();
            }

            class Derived : Base
            {
                Base inner;

                protected internal override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task InternalMethodsAreOverridden()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                internal abstract void Method();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                internal abstract void Method();
            }

            class Derived : Base
            {
                Base inner;

                internal override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PrivateProtectedMethodsCannotBeDelegatedThroughBaseType()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                private protected abstract void Method();
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """, [AnalyzersResources.Implement_abstract_class]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task PrivateProtectedMethodsCanBeDelegatedThroughSameType()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                private protected abstract void Method();
            }

            class [|Derived|] : Base
            {
                Derived inner;
            }
            """,
            """
            abstract class Base
            {
                private protected abstract void Method();
            }

            class Derived : Base
            {
                Derived inner;

                private protected override void Method()
                {
                    inner.Method();
                }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/41420")]
    public Task AccessorsWithDifferingVisibilityAreGeneratedCorrectly()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int InternalGet { internal get; set; }
                public abstract int InternalSet { get; internal set; }
            }

            class [|Derived|] : Base
            {
                Base inner;
            }
            """,
            """
            abstract class Base
            {
                public abstract int InternalGet { internal get; set; }
                public abstract int InternalSet { get; internal set; }
            }

            class Derived : Base
            {
                Base inner;

                public override int InternalGet { internal get => inner.InternalGet; set => inner.InternalGet = value; }
                public override int InternalSet { get => inner.InternalSet; internal set => inner.InternalSet = value; }
            }
            """, index: 1, new(title: string.Format(AnalyzersResources.Implement_through_0, "inner")));

    [Fact]
    public Task TestCrossProjectWithInaccessibleMemberInCase()
        => TestInRegularAndScriptAsync(
            """
            <Workspace>
                <Project Language="C#" AssemblyName="Assembly1" CommonReferences="true">
                    <Document>
            public abstract class Base
            {
                public abstract void Method1();
                internal abstract void Method2();
            }
                    </Document>
                </Project>
                <Project Language="C#" AssemblyName="Assembly2" CommonReferences="true">
                    <ProjectReference>Assembly1</ProjectReference>
                    <Document>
            class [|Derived|] : Base
            {
                Base inner;
            }
                    </Document>
                </Project>
            </Workspace>
            """,
            """
            <Workspace>
                <Project Language="C#" AssemblyName="Assembly1" CommonReferences="true">
                    <Document>
            public abstract class Base
            {
                public abstract void Method1();
                internal abstract void Method2();
            }
                    </Document>
                </Project>
                <Project Language="C#" AssemblyName="Assembly2" CommonReferences="true">
                    <ProjectReference>Assembly1</ProjectReference>
                    <Document>
            class {|Conflict:Derived|} : Base
            {
                Base inner;

                public override void Method1()
                {
                    inner.Method1();
                }
            }
                    </Document>
                </Project>
            </Workspace>
            """, index: 1);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam1()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
            }
            """,
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class Program(Base base1) : Base
            {
                public override int Method()
                {
                    return base1.Method();
                }
            }
            """, index: 1);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam2()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
                private Base _base = base1;
            }
            """, [AnalyzersResources.Implement_abstract_class, string.Format(AnalyzersResources.Implement_through_0, "_base")]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam2_B()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
                private Base _base = (base1);
            }
            """, [AnalyzersResources.Implement_abstract_class, string.Format(AnalyzersResources.Implement_through_0, "_base")]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam3()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
                private Base B { get; } = base1;
            }
            """, [AnalyzersResources.Implement_abstract_class, string.Format(AnalyzersResources.Implement_through_0, "B")]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam3_B()
        => TestExactActionSetOfferedAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
                private Base B { get; } = (base1);
            }
            """, [AnalyzersResources.Implement_abstract_class, string.Format(AnalyzersResources.Implement_through_0, "B")]);

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/69177")]
    public Task TestImplementThroughPrimaryConstructorParam4()
        => TestInRegularAndScriptAsync(
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class [|Program|](Base base1) : Base
            {
                private readonly int base1Hash = base1.GetHashCode();
            }
            """,
            """
            abstract class Base
            {
                public abstract int Method();
            }

            class Program(Base base1) : Base
            {
                private readonly int base1Hash = base1.GetHashCode();

                public override int Method()
                {
                    return base1.Method();
                }
            }
            """, index: 1);
}
