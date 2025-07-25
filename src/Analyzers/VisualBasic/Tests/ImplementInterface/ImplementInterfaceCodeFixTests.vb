﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports Microsoft.CodeAnalysis.CodeFixes
Imports Microsoft.CodeAnalysis.Diagnostics
Imports Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.Diagnostics
Imports Microsoft.CodeAnalysis.ImplementType
Imports Microsoft.CodeAnalysis.VisualBasic.ImplementInterface

Namespace Microsoft.CodeAnalysis.Editor.VisualBasic.UnitTests.ImplementInterface
    <Trait(Traits.Feature, Traits.Features.CodeActionsImplementInterface)>
    Partial Public Class ImplementInterfaceCodeFixTests
        Inherits AbstractVisualBasicDiagnosticProviderBasedUserDiagnosticTest_NoEditor

        Friend Overrides Function CreateDiagnosticProviderAndFixer(workspace As Workspace) As (DiagnosticAnalyzer, CodeFixProvider)
            Return (Nothing, New VisualBasicImplementInterfaceCodeFixProvider)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540085")>
        Public Async Function TestSimpleMethod() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I

    Public Sub M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestInterfaceWithTuple() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class Goo
    Implements [|IGoo|]
End Class
Interface IGoo
    Function Method(x As (Alice As Integer, Bob As Integer)) As (String, String)
End Interface",
"Imports System
Class Goo
    Implements IGoo

    Public Function Method(x As (Alice As Integer, Bob As Integer)) As (String, String) Implements IGoo.Method
        Throw New NotImplementedException()
    End Function
End Class
Interface IGoo
    Function Method(x As (Alice As Integer, Bob As Integer)) As (String, String)
End Interface")
        End Function

        <Fact>
        Public Async Function TestMethodConflict1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Function M() As Integer
    End Function
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I

    Private Sub I_M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub

    Function M() As Integer
    End Function
End Class")
        End Function

        <Fact>
        Public Async Function TestMethodConflict2() As Task
            Await TestInRegularAndScriptAsync(
"Interface IGoo
    Sub Bar()
End Interface
Class C
    Implements [|IGoo|]
    Public Sub Bar()
    End Sub
End Class",
"Interface IGoo
    Sub Bar()
End Interface
Class C
    Implements IGoo
    Public Sub Bar()
    End Sub

    Private Sub IGoo_Bar() Implements IGoo.Bar
        Bar()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542012")>
        Public Async Function TestMethodConflictWithField() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Private m As Integer
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I
    Private m As Integer

    Private Sub I_M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542015")>
        Public Async Function TestAutoPropertyConflict() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Property M As Integer
End Interface
Class C
    Implements [|I|]
    Public Property M As Integer
End Class",
"Interface I
    Property M As Integer
End Interface
Class C
    Implements I
    Public Property M As Integer

    Private Property I_M As Integer Implements I.M
        Get
            Return M
        End Get
        Set(value As Integer)
            M = Value
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542015")>
        Public Async Function TestFullPropertyConflict() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Property M As Integer
End Interface
Class C
    Implements [|I|]
    Private Property M As Integer
        Get
            Return 5
        End Get
        Set(value As Integer)
        End Set
    End Property
End Class",
"Interface I
    Property M As Integer
End Interface
Class C
    Implements I
    Private Property M As Integer
        Get
            Return 5
        End Get
        Set(value As Integer)
        End Set
    End Property

    Private Property I_M As Integer Implements I.M
        Get
            Return M
        End Get
        Set(value As Integer)
            M = Value
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542019")>
        Public Async Function TestConflictFromBaseClass1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class B
    Public Sub M()
    End Sub
End Class
Class C
    Inherits B
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
Class B
    Public Sub M()
    End Sub
End Class
Class C
    Inherits B
    Implements I

    Private Sub I_M() Implements I.M
        M()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542019")>
        Public Async Function TestConflictFromBaseClass2() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class B
    Protected M As Integer
End Class
Class C
    Inherits B
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
Class B
    Protected M As Integer
End Class
Class C
    Inherits B
    Implements I

    Private Sub I_M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542019")>
        Public Async Function TestConflictFromBaseClass3() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class B
    Public Property M As Integer
End Class
Class C
    Inherits B
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
Class B
    Public Property M As Integer
End Class
Class C
    Inherits B
    Implements I

    Private Sub I_M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementAbstractly1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
MustInherit Class C
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
MustInherit Class C
    Implements I

    Public MustOverride Sub M() Implements I.M
End Class",
index:=1)
        End Function

        <Fact>
        Public Async Function TestImplementGenericType() As Task
            Await TestInRegularAndScriptAsync(
"Interface IInterface1(Of T)
    Sub Method1(t As T)
End Interface
Class [Class]
    Implements [|IInterface1(Of Integer)|]
End Class",
"Interface IInterface1(Of T)
    Sub Method1(t As T)
End Interface
Class [Class]
    Implements IInterface1(Of Integer)

    Public Sub Method1(t As Integer) Implements IInterface1(Of Integer).Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementGenericTypeWithGenericMethod() As Task
            Await TestInRegularAndScriptAsync(
"Interface IInterface1(Of T)
    Sub Method1(Of U)(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements [|IInterface1(Of Integer)|]
End Class",
"Interface IInterface1(Of T)
    Sub Method1(Of U)(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements IInterface1(Of Integer)

    Public Sub Method1(Of U)(arg As Integer, arg1 As U) Implements IInterface1(Of Integer).Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem(6623, "DevDiv_Projects/Roslyn")>
        Public Async Function TestImplementGenericTypeWithGenericMethodWithNaturalConstraint() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Collections.Generic
Interface IInterface1(Of T)
    Sub Method1(Of U As IList(Of T))(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements [|IInterface1(Of Integer)|]
End Class",
"Imports System.Collections.Generic
Interface IInterface1(Of T)
    Sub Method1(Of U As IList(Of T))(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements IInterface1(Of Integer)

    Public Sub Method1(Of U As IList(Of Integer))(arg As Integer, arg1 As U) Implements IInterface1(Of Integer).Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem(6623, "DevDiv_Projects/Roslyn")>
        Public Async Function TestImplementGenericTypeWithGenericMethodWithUnexpressibleConstraint() As Task
            Await TestInRegularAndScriptAsync(
"Interface IInterface1(Of T)
    Sub Method1(Of U As T)(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements [|IInterface1(Of Integer)|]
End Class",
"Interface IInterface1(Of T)
    Sub Method1(Of U As T)(arg As T, arg1 As U)
End Interface
Class [Class]
    Implements IInterface1(Of Integer)

    Public Sub Method1(Of U As Integer)(arg As Integer, arg1 As U) Implements IInterface1(Of Integer).Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementThroughFieldMember() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Private x As I
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I
    Private x As I

    Public Sub M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementThroughFieldMember1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Private x As I
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I
    Private x As I

    Public Sub M() Implements I.M
        x.M()
    End Sub
End Class",
index:=1)
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/472")>
        Public Async Function TestImplementThroughFieldMemberRemoveUnnecessaryCast() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Collections

NotInheritable Class X : Implements [|IComparer|]
    Private x As X
End Class",
"Imports System.Collections

NotInheritable Class X : Implements IComparer
    Private x As X

    Public Function Compare(x As Object, y As Object) As Integer Implements IComparer.Compare
        Return DirectCast(Me.x, IComparer).Compare(x, y)
    End Function
End Class",
index:=1)
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/472")>
        Public Async Function TestImplementThroughFieldMemberRemoveUnnecessaryCastAndMe() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Collections

NotInheritable Class X : Implements [|IComparer|]
    Private a As X
End Class",
"Imports System.Collections

NotInheritable Class X : Implements IComparer
    Private a As X

    Public Function Compare(x As Object, y As Object) As Integer Implements IComparer.Compare
        Return DirectCast(a, IComparer).Compare(x, y)
    End Function
End Class",
index:=1)
        End Function

        <Fact>
        Public Async Function TestImplementThroughFieldMemberInterfaceWithNonStandardProperties() As Task
            Dim source =
<File>
Interface IGoo
    Property Blah(x As Integer) As Integer
    Default Property Blah1(x As Integer) As Integer
End Interface

Class C
    Implements [|IGoo|]
    Dim i1 As IGoo
End Class
</File>
            Dim expected =
<File>
Interface IGoo
    Property Blah(x As Integer) As Integer
    Default Property Blah1(x As Integer) As Integer
End Interface

Class C
    Implements IGoo
    Dim i1 As IGoo

    Public Property Blah(x As Integer) As Integer Implements IGoo.Blah
        Get
            Return i1.Blah(x)
        End Get
        Set(value As Integer)
            i1.Blah(x) = value
        End Set
    End Property

    Default Public Property Blah1(x As Integer) As Integer Implements IGoo.Blah1
        Get
            Return i1(x)
        End Get
        Set(value As Integer)
            i1(x) = value
        End Set
    End Property
End Class
</File>
            Await TestAsync(source, expected, index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540355")>
        Public Async Function TestMissingOnImplementationWithDifferentName() As Task
            Await TestMissingInRegularAndScriptAsync(
"Interface I1(Of T)
    Function Goo() As Double
End Interface
Class M
    Implements [|I1(Of Double)|]
    Public Function I_Goo() As Double Implements I1(Of Double).Goo
        Return 2
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540366")>
        Public Async Function TestWithMissingEndBlock() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class M
    Implements [|IServiceProvider|]",
"Imports System
Class M
    Implements IServiceProvider

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class
")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540367")>
        Public Async Function TestSimpleProperty() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Property Goo() As Integer
End Interface
Class M
    Implements [|I1|]
End Class",
"Interface I1
    Property Goo() As Integer
End Interface
Class M
    Implements I1

    Public Property Goo As Integer Implements I1.Goo
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property
End Class")
        End Function

        <Fact>
        Public Async Function TestArrayType() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Function M() As String()
End Interface
Class C
    Implements [|I|]
End Class",
"Interface I
    Function M() As String()
End Interface
Class C
    Implements I

    Public Function M() As String() Implements I.M
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceWithByRefParameters() As Task
            Await TestInRegularAndScriptAsync(
"Class C
    Implements [|I|]
    Private goo As I
End Class
Interface I
    Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer)
    Function Method2() As Integer
End Interface",
"Class C
    Implements I
    Private goo As I

    Public Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer) Implements I.Method1
        Throw New System.NotImplementedException()
    End Sub

    Public Function Method2() As Integer Implements I.Method2
        Throw New System.NotImplementedException()
    End Function
End Class
Interface I
    Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer)
    Function Method2() As Integer
End Interface")
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceWithTypeCharacter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Function Method1$()
End Interface
Class C
    Implements [|I1|]
End Class",
"Interface I1
    Function Method1$()
End Interface
Class C
    Implements I1

    Public Function Method1() As String Implements I1.Method1
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceWithParametersTypeSpecifiedAsTypeCharacter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Sub Method1(ByRef arg#)
End Interface
Class C
    Implements [|I1|]
End Class",
"Interface I1
    Sub Method1(ByRef arg#)
End Interface
Class C
    Implements I1

    Public Sub Method1(ByRef arg As Double) Implements I1.Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540403")>
        Public Async Function TestMissingOnInterfaceWithJustADelegate() As Task
            Await TestMissingInRegularAndScriptAsync(
"Interface I1
    Delegate Sub Del()
End Interface
Class C
    Implements [|I1|]
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540381")>
        Public Async Function TestOrdering1() As Task
            Await TestInRegularAndScriptAsync(
"Class C
    Implements [|I|]
    Private goo As I
End Class
Interface I
    Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer)
    Function Method2() As Integer
End Interface",
"Class C
    Implements I
    Private goo As I

    Public Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer) Implements I.Method1
        Throw New System.NotImplementedException()
    End Sub

    Public Function Method2() As Integer Implements I.Method2
        Throw New System.NotImplementedException()
    End Function
End Class
Interface I
    Sub Method1(ByRef x As Integer, ByRef y As Integer, z As Integer)
    Function Method2() As Integer
End Interface")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540415")>
        Public Async Function TestDefaultProperty1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Default Property Goo(ByVal arg As Integer)
End Interface
Class C
    Implements [|I1|]
End Class",
"Interface I1
    Default Property Goo(ByVal arg As Integer)
End Interface
Class C
    Implements I1

    Default Public Property Goo(arg As Integer) As Object Implements I1.Goo
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Object)
            Throw New System.NotImplementedException()
        End Set
    End Property
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementNestedInterface() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Sub Goo()
    Delegate Sub Del(ByVal arg As Integer)
    Interface I2
        Sub Goo(ByVal arg As Del)
    End Interface
End Interface
Class C
    Implements [|I1.I2|]
End Class",
"Interface I1
    Sub Goo()
    Delegate Sub Del(ByVal arg As Integer)
    Interface I2
        Sub Goo(ByVal arg As Del)
    End Interface
End Interface
Class C
    Implements I1.I2

    Public Sub Goo(arg As I1.Del) Implements I1.I2.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540402")>
        Public Async Function TestArrayRankSpecifiers() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Sub Method1(ByVal arg() As Integer)
End Interface
Class C
    Implements [|I1|]
End Class",
"Interface I1
    Sub Method1(ByVal arg() As Integer)
End Interface
Class C
    Implements I1

    Public Sub Method1(arg() As Integer) Implements I1.Method1
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540398")>
        Public Async Function TestSimplifyImplementsClause() As Task
            Await TestAsync(
"Namespace ConsoleApplication
    Interface I1
        Sub Method1()
    End Interface
    Class C
        Implements [|I1|]
    End Class
End Namespace",
"Namespace ConsoleApplication
    Interface I1
        Sub Method1()
    End Interface
    Class C
        Implements I1

        Public Sub Method1() Implements I1.Method1
            Throw New System.NotImplementedException()
        End Sub
    End Class
End Namespace",
New TestParameters(parseOptions:=Nothing)) ' Namespaces not supported in script
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/541078")>
        Public Async Function TestParamArray() As Task
            Await TestInRegularAndScriptAsync(
"Interface I2
    Function G(ParamArray args As Double()) As Integer
End Interface
Class A
    Implements [|I2|]
End Class",
"Interface I2
    Function G(ParamArray args As Double()) As Integer
End Interface
Class A
    Implements I2

    Public Function G(ParamArray args() As Double) As Integer Implements I2.G
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact>
        Public Async Function TestDoNotShowForNonImplementedPrivateInterfaceMethod() As Task
            Await TestMissingInRegularAndScriptAsync(
"Interface I1
    Private Sub Goo()
End Interface
Class A
    Implements [|I1|]
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/541092")>
        Public Async Function TestDoNotShowForImplementedPrivateInterfaceMethod() As Task
            Await TestMissingInRegularAndScriptAsync(
"Interface I1
    Private Sub Goo()
End Interface
Class A
    Implements [|I1|]
    Public Sub Goo() Implements I1.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542010")>
        Public Async Function TestNoImplementThroughSynthesizedFields() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Public Property X As I
End Class",
count:=2)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Public Property X As I
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I
    Public Property X As I

    Public Sub M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class C
    Implements [|I|]
    Public Property X As I
End Class",
"Interface I
    Sub M()
End Interface
Class C
    Implements I
    Public Property X As I

    Public Sub M() Implements I.M
        X.M()
    End Sub
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementIReadOnlyListThroughField() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Collections.Generic
Class A
    Implements [|IReadOnlyList(Of Integer)|]
    Private field As Integer()
End Class",
"Imports System.Collections
Imports System.Collections.Generic
Class A
    Implements IReadOnlyList(Of Integer)
    Private field As Integer()

    Default Public ReadOnly Property Item(index As Integer) As Integer Implements IReadOnlyList(Of Integer).Item
        Get
            Return DirectCast(field, IReadOnlyList(Of Integer))(index)
        End Get
    End Property

    Public ReadOnly Property Count As Integer Implements IReadOnlyCollection(Of Integer).Count
        Get
            Return DirectCast(field, IReadOnlyCollection(Of Integer)).Count
        End Get
    End Property

    Public Function GetEnumerator() As IEnumerator(Of Integer) Implements IEnumerable(Of Integer).GetEnumerator
        Return DirectCast(field, IEnumerable(Of Integer)).GetEnumerator()
    End Function

    Private Function IEnumerable_GetEnumerator() As IEnumerator Implements IEnumerable.GetEnumerator
        Return field.GetEnumerator()
    End Function
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementIReadOnlyListThroughProperty() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Collections.Generic
Class A
    Implements [|IReadOnlyList(Of Integer)|]
    Private Property field As Integer()
End Class",
"Imports System.Collections
Imports System.Collections.Generic
Class A
    Implements IReadOnlyList(Of Integer)

    Default Public ReadOnly Property Item(index As Integer) As Integer Implements IReadOnlyList(Of Integer).Item
        Get
            Return DirectCast(field, IReadOnlyList(Of Integer))(index)
        End Get
    End Property

    Public ReadOnly Property Count As Integer Implements IReadOnlyCollection(Of Integer).Count
        Get
            Return DirectCast(field, IReadOnlyCollection(Of Integer)).Count
        End Get
    End Property

    Private Property field As Integer()

    Public Function GetEnumerator() As IEnumerator(Of Integer) Implements IEnumerable(Of Integer).GetEnumerator
        Return DirectCast(field, IEnumerable(Of Integer)).GetEnumerator()
    End Function

    Private Function IEnumerable_GetEnumerator() As IEnumerator Implements IEnumerable.GetEnumerator
        Return field.GetEnumerator()
    End Function
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementInterfaceThroughField() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    Dim x As A
End Class",
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I
    Dim x As A

    Public Sub M() Implements I.M
        DirectCast(x, I).M()
    End Sub
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementInterfaceThroughField_FieldImplementsMultipleInterfaces() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements [|I|]
    Implements I2
    Dim x As A
End Class",
count:=2)

            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements I
    Implements [|I2|]
    Dim x As A
End Class",
count:=2)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements [|I|]
    Implements I2
    Dim x As A
End Class",
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements I
    Implements I2
    Dim x As A

    Public Sub M() Implements I.M
        DirectCast(x, I).M()
    End Sub
End Class",
index:=1)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements I
    Implements [|I2|]
    Dim x As A
End Class",
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I, I2
    Public Sub M() Implements I.M, I2.M2
    End Sub
End Class

Class B
    Implements I
    Implements I2
    Dim x As A

    Public Sub M2() Implements I2.M2
        DirectCast(x, I2).M2()
    End Sub
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementInterfaceThroughField_MultipleFieldsCanImplementInterface() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    Dim x As A
    Dim y As A
End Class",
count:=3)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    Dim x As A
    Dim y As A
End Class",
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I
    Dim x As A
    Dim y As A

    Public Sub M() Implements I.M
        DirectCast(x, I).M()
    End Sub
End Class",
index:=1)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    Dim x As A
    Dim y As A
End Class",
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I
    Dim x As A
    Dim y As A

    Public Sub M() Implements I.M
        DirectCast(y, I).M()
    End Sub
End Class",
index:=2)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestImplementInterfaceThroughField_MultipleFieldsForMultipleInterfaces() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements [|I|]
    Implements I2
    Dim x As A
    Dim y as B
End Class",
count:=2)

            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements I
    Implements [|I2|]
    Dim x As A
    Dim y as B
End Class",
count:=2)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements [|I|]
    Implements I2
    Dim x As A
    Dim y as B
End Class",
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements I
    Implements I2
    Dim x As A
    Dim y as B

    Public Sub M() Implements I.M
        DirectCast(x, I).M()
    End Sub
End Class",
index:=1)

            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements I
    Implements [|I2|]
    Dim x As A
    Dim y as B
End Class",
"Interface I
    Sub M()
End Interface
Interface I2
    Sub M2()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements I2
    Public Sub M2() Implements I2.M2
    End Sub
End Class

Class C
    Implements I
    Implements I2
    Dim x As A
    Dim y as B

    Public Sub M2() Implements I2.M2
        DirectCast(y, I2).M2()
    End Sub
End Class",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestNoImplementThroughDefaultProperty() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    Default ReadOnly Property x(index as Integer) As A
        Get
            Return Nothing
        End Get
    End Property
End Class",
count:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestNoImplementThroughParameterizedProperty() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    ReadOnly Property x(index as Integer) As A
        Get
            Return Nothing
        End Get
    End Property
End Class",
count:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/768799")>
        Public Async Function TestNoImplementThroughWriteOnlyProperty() As Task
            Await TestActionCountAsync(
"Interface I
    Sub M()
End Interface
Class A
    Implements I
    Public Sub M() Implements I.M
    End Sub
End Class

Class B
    Implements [|I|]
    WriteOnly Property x(index as Integer) As A
        Set(value as A)
        End Set
    End Property
End Class",
count:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/540469")>
        Public Async Function TestInsertBlankLineAfterImplementsAndInherits() As Task
            Await TestInRegularAndScriptAsync(
<Text>Interface I1
    Function Goo()
End Interface

Class M
    Implements [|I1|]
End Class</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Interface I1
    Function Goo()
End Interface

Class M
    Implements I1

    Public Function Goo() As Object Implements I1.Goo
        Throw New System.NotImplementedException()
    End Function
End Class</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542290")>
        Public Async Function TestMethodShadowsProperty() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub M()
End Interface
Class B
    'Protected m As Integer 
    Public Property M As Integer
End Class
Class C
    Inherits B
    Implements [|I|]
End Class",
"Interface I
    Sub M()
End Interface
Class B
    'Protected m As Integer 
    Public Property M As Integer
End Class
Class C
    Inherits B
    Implements I

    Private Sub I_M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/542606")>
        Public Async Function TestRemMethod() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub [Rem]
End Interface
Class C
    Implements [|I|] ' Implement interface 
End Class",
"Interface I
    Sub [Rem]
End Interface
Class C
    Implements I ' Implement interface 

    Public Sub [Rem]() Implements I.[Rem]
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/543425")>
        Public Async Function TestMissingIfEventAlreadyImplemented() As Task
            Await TestMissingInRegularAndScriptAsync(
"Imports System.ComponentModel
Class C
    Implements [|INotifyPropertyChanged|]
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/543506")>
        Public Async Function TestAddEvent1() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.ComponentModel
Class C
    Implements [|INotifyPropertyChanged|]
End Class",
"Imports System.ComponentModel
Class C
    Implements INotifyPropertyChanged

    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementThroughMemberEvent() As Task
            Await TestInRegularAndScriptAsync("
Imports System.ComponentModel

Class Worker
	Implements INotifyPropertyChanged

	Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class

Class Boss
	Implements [|INotifyPropertyChanged|]

	Private worker As Worker
End Class", "
Imports System.ComponentModel

Class Worker
	Implements INotifyPropertyChanged

	Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
End Class

Class Boss
	Implements INotifyPropertyChanged

	Private worker As Worker

    Public Custom Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        AddHandler(value As PropertyChangedEventHandler)
            AddHandler DirectCast(worker, INotifyPropertyChanged).PropertyChanged, value
        End AddHandler
        RemoveHandler(value As PropertyChangedEventHandler)
            RemoveHandler DirectCast(worker, INotifyPropertyChanged).PropertyChanged, value
        End RemoveHandler
        RaiseEvent(sender As Object, e As PropertyChangedEventArgs)
        End RaiseEvent
    End Event
End Class", index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/543588")>
        Public Async Function TestNameSimplifyGenericType() As Task
            Await TestInRegularAndScriptAsync(
"Interface I(Of In T, Out R)
    Sub Goo()
End Interface
Class C(Of T, R)
    Implements [|I(Of T, R)|]
End Class",
"Interface I(Of In T, Out R)
    Sub Goo()
End Interface
Class C(Of T, R)
    Implements I(Of T, R)

    Public Sub Goo() Implements I(Of T, R).Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544156")>
        Public Async Function TestInterfacePropertyRedefinition() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Property Bar As Integer
End Interface
Interface I2
    Inherits I1
    Property Bar As Integer
End Interface
Class C
    Implements [|I2|]
End Class",
"Interface I1
    Property Bar As Integer
End Interface
Interface I2
    Inherits I1
    Property Bar As Integer
End Interface
Class C
    Implements I2

    Public Property Bar As Integer Implements I2.Bar
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property

    Private Property I1_Bar As Integer Implements I1.Bar
        Get
            Return Bar
        End Get
        Set(value As Integer)
            Bar = Value
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544208")>
        Public Async Function TestMissingOnWrongArity() As Task
            Await TestMissingInRegularAndScriptAsync(
"Interface I1(Of T)
    ReadOnly Property Bar As Integer
End Interface
Class C
    Implements [|I1|]
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529328")>
        Public Async Function TestPropertyShadowing() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Property Bar As Integer
    Sub Goo()
End Interface
Class B
    Public Property Bar As Integer
End Class
Class C
    Inherits B
    Implements [|I1|]
End Class",
"Interface I1
    Property Bar As Integer
    Sub Goo()
End Interface
Class B
    Public Property Bar As Integer
End Class
Class C
    Inherits B
    Implements I1

    Private Property I1_Bar As Integer Implements I1.Bar
        Get
            Return Bar
        End Get
        Set(value As Integer)
            Bar = Value
        End Set
    End Property

    Public Sub Goo() Implements I1.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544206")>
        Public Async Function TestEventWithImplicitDelegateCreation() As Task
            Await TestInRegularAndScriptAsync(
"Interface I1
    Event E(x As String)
End Interface
Class C
    Implements [|I1|]
End Class",
"Interface I1
    Event E(x As String)
End Interface
Class C
    Implements I1

    Public Event E(x As String) Implements I1.E
End Class")
        End Function

        <Fact>
        Public Async Function TestStringLiteral() As Task
            Await TestInRegularAndScriptAsync(
"Interface IGoo
    Sub Goo(Optional s As String = """""""")
End Interface
Class Bar
    Implements [|IGoo|]
End Class",
"Interface IGoo
    Sub Goo(Optional s As String = """""""")
End Interface
Class Bar
    Implements IGoo

    Public Sub Goo(Optional s As String = """""""") Implements IGoo.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545643"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestVBConstantValue1() As Task
            Await TestInRegularAndScriptAsync(
"Imports Microsoft.VisualBasic
Interface I
    Sub VBNullChar(Optional x As String = Constants.vbNullChar)
End Interface

Class C
    Implements [|I|]
End Class",
"Imports Microsoft.VisualBasic
Interface I
    Sub VBNullChar(Optional x As String = Constants.vbNullChar)
End Interface

Class C
    Implements I

    Public Sub VBNullChar(Optional x As String = Constants.vbNullChar) Implements I.VBNullChar
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestVBConstantValue2() As Task
            Await TestAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub VBNullChar(Optional x As String = Constants.vbNullChar)
End Interface

Namespace N
    Class Microsoft
        Implements [|I|]
    End Class
End Namespace",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub VBNullChar(Optional x As String = Constants.vbNullChar)
End Interface

Namespace N
    Class Microsoft
        Implements I

        Public Sub VBNullChar(Optional x As String = Constants.vbNullChar) Implements I.VBNullChar
            Throw New NotImplementedException()
        End Sub
    End Class
End Namespace",
New TestParameters(parseOptions:=Nothing))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545679"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestVBConstantValue3() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub ChrW(Optional x As String = Strings.ChrW(1))
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub ChrW(Optional x As String = Strings.ChrW(1))
End Interface

Class C
    Implements I

    Public Sub ChrW(Optional x As String = Strings.ChrW(1)) Implements I.ChrW
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545674")>
        Public Async Function TestDateTimeLiteral1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Date = #6/29/2012#)
End Interface
Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Date = #6/29/2012#)
End Interface
Class C
    Implements I

    Public Sub Goo(Optional x As Date = #6/29/2012 12:00:00 AM#) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545675"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestEnumConstant1() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek = DayOfWeek.Friday)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek = DayOfWeek.Friday)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As DayOfWeek = DayOfWeek.Friday) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545644")>
        Public Async Function TestMultiDimensionalArray1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(x As Integer()())
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(x As Integer()())
End Interface

Class C
    Implements I

    Public Sub Goo(x As Integer()()) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545640"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestUnicodeQuote() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(8220))
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(8220))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As String = ChrW(8220)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545563")>
        Public Async Function TestLongMinValue() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Long = Long.MinValue)
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Long = Long.MinValue)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Long = Long.MinValue) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestMinMaxValues() As Task
            Await TestInRegularAndScriptAsync(
<Text>Interface I
    Sub M01(Optional x As Short = Short.MinValue)
    Sub M02(Optional x As Short = Short.MaxValue)
    Sub M03(Optional x As UShort = UShort.MinValue)
    Sub M04(Optional x As UShort = UShort.MaxValue)
    Sub M05(Optional x As Integer = Integer.MinValue)
    Sub M06(Optional x As Integer = Integer.MaxValue)
    Sub M07(Optional x As UInteger = UInteger.MinValue)
    Sub M08(Optional x As UInteger = UInteger.MaxValue)
    Sub M09(Optional x As Long = Long.MinValue)
    Sub M10(Optional x As Long = Long.MaxValue)
    Sub M11(Optional x As ULong = ULong.MinValue)
    Sub M12(Optional x As ULong = ULong.MaxValue)
End Interface

Class C
    Implements [|I|]
End Class</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Interface I
    Sub M01(Optional x As Short = Short.MinValue)
    Sub M02(Optional x As Short = Short.MaxValue)
    Sub M03(Optional x As UShort = UShort.MinValue)
    Sub M04(Optional x As UShort = UShort.MaxValue)
    Sub M05(Optional x As Integer = Integer.MinValue)
    Sub M06(Optional x As Integer = Integer.MaxValue)
    Sub M07(Optional x As UInteger = UInteger.MinValue)
    Sub M08(Optional x As UInteger = UInteger.MaxValue)
    Sub M09(Optional x As Long = Long.MinValue)
    Sub M10(Optional x As Long = Long.MaxValue)
    Sub M11(Optional x As ULong = ULong.MinValue)
    Sub M12(Optional x As ULong = ULong.MaxValue)
End Interface

Class C
    Implements I

    Public Sub M01(Optional x As Short = Short.MinValue) Implements I.M01
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M02(Optional x As Short = Short.MaxValue) Implements I.M02
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M03(Optional x As UShort = 0) Implements I.M03
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M04(Optional x As UShort = UShort.MaxValue) Implements I.M04
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M05(Optional x As Integer = Integer.MinValue) Implements I.M05
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M06(Optional x As Integer = Integer.MaxValue) Implements I.M06
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M07(Optional x As UInteger = 0) Implements I.M07
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M08(Optional x As UInteger = UInteger.MaxValue) Implements I.M08
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M09(Optional x As Long = Long.MinValue) Implements I.M09
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M10(Optional x As Long = Long.MaxValue) Implements I.M10
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M11(Optional x As ULong = 0) Implements I.M11
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M12(Optional x As ULong = ULong.MaxValue) Implements I.M12
        Throw New System.NotImplementedException()
    End Sub
End Class</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact>
        Public Async Function TestFloatConstants() As Task
            Await TestInRegularAndScriptAsync(
<Text>Interface I
    Sub D1(Optional x As Double = Double.Epsilon)
    Sub D2(Optional x As Double = Double.MaxValue)
    Sub D3(Optional x As Double = Double.MinValue)
    Sub D4(Optional x As Double = Double.NaN)
    Sub D5(Optional x As Double = Double.NegativeInfinity)
    Sub D6(Optional x As Double = Double.PositiveInfinity)
    Sub S1(Optional x As Single = Single.Epsilon)
    Sub S2(Optional x As Single = Single.MaxValue)
    Sub S3(Optional x As Single = Single.MinValue)
    Sub S4(Optional x As Single = Single.NaN)
    Sub S5(Optional x As Single = Single.NegativeInfinity)
    Sub S6(Optional x As Single = Single.PositiveInfinity)
End Interface

Class C
    Implements [|I|]
End Class</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Interface I
    Sub D1(Optional x As Double = Double.Epsilon)
    Sub D2(Optional x As Double = Double.MaxValue)
    Sub D3(Optional x As Double = Double.MinValue)
    Sub D4(Optional x As Double = Double.NaN)
    Sub D5(Optional x As Double = Double.NegativeInfinity)
    Sub D6(Optional x As Double = Double.PositiveInfinity)
    Sub S1(Optional x As Single = Single.Epsilon)
    Sub S2(Optional x As Single = Single.MaxValue)
    Sub S3(Optional x As Single = Single.MinValue)
    Sub S4(Optional x As Single = Single.NaN)
    Sub S5(Optional x As Single = Single.NegativeInfinity)
    Sub S6(Optional x As Single = Single.PositiveInfinity)
End Interface

Class C
    Implements I

    Public Sub D1(Optional x As Double = Double.Epsilon) Implements I.D1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub D2(Optional x As Double = Double.MaxValue) Implements I.D2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub D3(Optional x As Double = Double.MinValue) Implements I.D3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub D4(Optional x As Double = Double.NaN) Implements I.D4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub D5(Optional x As Double = Double.NegativeInfinity) Implements I.D5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub D6(Optional x As Double = Double.PositiveInfinity) Implements I.D6
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S1(Optional x As Single = Single.Epsilon) Implements I.S1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S2(Optional x As Single = Single.MaxValue) Implements I.S2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S3(Optional x As Single = Single.MinValue) Implements I.S3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S4(Optional x As Single = Single.NaN) Implements I.S4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S5(Optional x As Single = Single.NegativeInfinity) Implements I.S5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S6(Optional x As Single = Single.PositiveInfinity) Implements I.S6
        Throw New System.NotImplementedException()
    End Sub
End Class</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestEnumParameters() As Task
            Await TestInRegularAndScriptAsync(
<Text><![CDATA[Imports System

Enum E
    A = 1
    B = 2
End Enum

<FlagsAttribute>
Enum FlagE
    A = 1
    B = 2
End Enum

Interface I
    Sub M1(Optional e As E = E.A Or E.B)
    Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B)
End Interface

Class C
    Implements [|I|]
End Class]]></Text>.Value.Replace(vbLf, vbCrLf),
<Text><![CDATA[Imports System

Enum E
    A = 1
    B = 2
End Enum

<FlagsAttribute>
Enum FlagE
    A = 1
    B = 2
End Enum

Interface I
    Sub M1(Optional e As E = E.A Or E.B)
    Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B)
End Interface

Class C
    Implements I

    Public Sub M1(Optional e As E = 3) Implements I.M1
        Throw New NotImplementedException()
    End Sub

    Public Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B) Implements I.M2
        Throw New NotImplementedException()
    End Sub
End Class]]></Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestEnumParameters2() As Task
            Await TestInRegularAndScriptAsync(
<Text><![CDATA[
Option Strict On
Imports System

Enum E
    A = 1
    B = 2
End Enum

<FlagsAttribute>
Enum FlagE
    A = 1
    B = 2
End Enum

Interface I
    Sub M1(Optional e As E = E.A Or E.B)
    Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B)
End Interface

Class C
    Implements [|I|]
End Class]]></Text>.Value.Replace(vbLf, vbCrLf),
<Text><![CDATA[
Option Strict On
Imports System

Enum E
    A = 1
    B = 2
End Enum

<FlagsAttribute>
Enum FlagE
    A = 1
    B = 2
End Enum

Interface I
    Sub M1(Optional e As E = E.A Or E.B)
    Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B)
End Interface

Class C
    Implements I

    Public Sub M1(Optional e As E = CType(3, E)) Implements I.M1
        Throw New NotImplementedException()
    End Sub

    Public Sub M2(Optional e As FlagE = FlagE.A Or FlagE.B) Implements I.M2
        Throw New NotImplementedException()
    End Sub
End Class]]></Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545691")>
        Public Async Function TestMultiDimArray1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(x As Integer(,))
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(x As Integer(,))
End Interface

Class C
    Implements I

    Public Sub Goo(x(,) As Integer) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545640"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestQuoteEscaping1() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = ChrW(8220))
End Interface
Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = ChrW(8220))
End Interface
Class C
    Implements I

    Public Sub Goo(Optional x As Char = ChrW(8220)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545866")>
        Public Async Function TestQuoteEscaping2() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Object = ""‟"")
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Object = ""‟"")
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = ""‟"") Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545689")>
        Public Async Function TestDecimalLiteral1() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Decimal = Decimal.MaxValue)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Decimal = Decimal.MaxValue)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Decimal = Decimal.MaxValue) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545687"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestRemoveParenthesesAroundTypeReference1() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek = DayOfWeek.Monday)
End Interface

Class C
    Implements [|I|]

    Property DayOfWeek As DayOfWeek
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek = DayOfWeek.Monday)
End Interface

Class C
    Implements I

    Property DayOfWeek As DayOfWeek

    Public Sub Goo(Optional x As DayOfWeek = DayOfWeek.Monday) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545694"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestNullableDefaultValue1() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek? = DayOfWeek.Friday)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As DayOfWeek? = DayOfWeek.Friday)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As DayOfWeek? = DayOfWeek.Friday) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        ' TODO: Enable test on .NET Core
        ' https://github.com/dotnet/roslyn/issues/71625
        <ConditionalFact(GetType(DesktopOnly)), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545688")>
        Public Async Function TestHighPrecisionDouble() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Interface I
    Sub Goo(Optional x As Double = 2.8025969286496341E-45)
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Interface I
    Sub Goo(Optional x As Double = 2.8025969286496341E-45)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Double = 2.8025969286496341E-45) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545729"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestCharSurrogates() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = ChrW(55401))
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = ChrW(55401))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Char = ChrW(55401)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545733"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestReservedChar() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = Chr(13))
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Char = Chr(13))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Char = ChrW(13)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545685")>
        Public Async Function TestCastEnumValue() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As ConsoleColor = CType(-1, ConsoleColor))
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As ConsoleColor = CType(-1, ConsoleColor))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As ConsoleColor = CType(-1, ConsoleColor)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545756")>
        Public Async Function TestArrayOfNullables() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(x As Integer?())
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(x As Integer?())
End Interface

Class C
    Implements I

    Public Sub Goo(x As Integer?()) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545753")>
        Public Async Function TestOptionalArrayParameterWithDefault() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Integer() = Nothing)
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Integer() = Nothing)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x() As Integer = Nothing) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545742"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestRemFieldEnum() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Enum E
    [Rem]
End Enum

Interface I
    Sub Goo(Optional x As E = E.[Rem])
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Enum E
    [Rem]
End Enum

Interface I
    Sub Goo(Optional x As E = E.[Rem])
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As E = E.[Rem]) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545790")>
        Public Async Function TestByteParameter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Byte = 1)
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Byte = 1)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Byte = 1) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545789")>
        Public Async Function TestDefaultParameterSuffix1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Object = 1L)
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Object = 1L)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = 1L) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545809")>
        Public Async Function TestZeroValuedEnum() As Task
            Await TestInRegularAndScriptAsync(
"Enum E
    A = 1
End Enum

Interface I
    Sub Goo(Optional x As E = 0)
End Interface

Class C
    Implements [|I|]
End Class",
"Enum E
    A = 1
End Enum

Interface I
    Sub Goo(Optional x As E = 0)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As E = 0) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545824")>
        Public Async Function TestByteCast() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As Object = CByte(1))
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As Object = CByte(1))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = CByte(1)) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545825")>
        Public Async Function TestDecimalValues() As Task
            Await TestInRegularAndScriptAsync(
<Text>Option Strict On

Interface I
    Sub M1(Optional x As Decimal = 2D)
    Sub M2(Optional x As Decimal = 2.0D)
    Sub M3(Optional x As Decimal = 0D)
    Sub M4(Optional x As Decimal = 0.0D)
    Sub M5(Optional x As Decimal = 0.1D)
    Sub M6(Optional x As Decimal = 0.10D)
End Interface
 
Class C
    Implements [|I|]
End Class
</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Option Strict On

Interface I
    Sub M1(Optional x As Decimal = 2D)
    Sub M2(Optional x As Decimal = 2.0D)
    Sub M3(Optional x As Decimal = 0D)
    Sub M4(Optional x As Decimal = 0.0D)
    Sub M5(Optional x As Decimal = 0.1D)
    Sub M6(Optional x As Decimal = 0.10D)
End Interface
 
Class C
    Implements I

    Public Sub M1(Optional x As Decimal = 2) Implements I.M1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M2(Optional x As Decimal = 2.0D) Implements I.M2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M3(Optional x As Decimal = 0) Implements I.M3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M4(Optional x As Decimal = 0.0D) Implements I.M4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M5(Optional x As Decimal = 0.1D) Implements I.M5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M6(Optional x As Decimal = 0.10D) Implements I.M6
        Throw New System.NotImplementedException()
    End Sub
End Class
</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545693")>
        Public Async Function TestSmallDecimal() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On

Interface I
    Sub Goo(Optional x As Decimal = Long.MinValue)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On

Interface I
    Sub Goo(Optional x As Decimal = Long.MinValue)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Decimal = -9223372036854775808D) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545771")>
        Public Async Function TestEventConflict() As Task
            Await TestInRegularAndScriptAsync(
"Interface IA
    Event E As EventHandler
End Interface
Interface IB
    Inherits IA
    Shadows Event E As Action
End Interface
Class C
    Implements [|IB|]
End Class",
"Interface IA
    Event E As EventHandler
End Interface
Interface IB
    Inherits IA
    Shadows Event E As Action
End Interface
Class C
    Implements IB

    Public Event E As Action Implements IB.E
    Private Event IA_E As EventHandler Implements IA.E
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545826")>
        Public Async Function TestDecimalField() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Object = 1D)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Object = 1D)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = 1D) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545827")>
        Public Async Function TestDoubleInObjectContext() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Object = 1.0)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Object = 1.0)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = 1R) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545860")>
        Public Async Function TestLargeDecimal() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Decimal = 10000000000000000000D)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As Decimal = 10000000000000000000D)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Decimal = 10000000000000000000D) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545870")>
        Public Async Function TestSurrogatePair1() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Optional x As String = ""𪛖"")
End Interface
Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Optional x As String = ""𪛖"")
End Interface
Class C
    Implements I

    Public Sub Goo(Optional x As String = ""𪛖"") Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545893"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestVBTab() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = vbTab)
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = vbTab)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As String = vbTab) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545912")>
        Public Async Function TestEscapeTypeParameter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Of [TO], TP, TQ)()
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Of [TO], TP, TQ)()
End Interface

Class C
    Implements I

    Public Sub Goo(Of [TO], TP, TQ)() Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545892")>
        Public Async Function TestLargeUnsignedLong() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As ULong = 10000000000000000000UL)
End Interface

Class C
    Implements [|I|]
End Class",
"Option Strict On
Imports System
Interface I
    Sub Goo(Optional x As ULong = 10000000000000000000UL)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As ULong = 10000000000000000000UL) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545865")>
        Public Async Function TestSmallDecimalValues() As Task
            Dim markup =
<File>
Option Strict On

Interface I
    Sub F1(Optional x As Decimal = 1E-25D)
    Sub F2(Optional x As Decimal = 1E-26D)
    Sub F3(Optional x As Decimal = 1E-27D)
    Sub F4(Optional x As Decimal = 1E-28D)
    Sub F5(Optional x As Decimal = 1E-29D)
    Sub M1(Optional x As Decimal = 1.1E-25D)
    Sub M2(Optional x As Decimal = 1.1E-26D)
    Sub M3(Optional x As Decimal = 1.1E-27D)
    Sub M4(Optional x As Decimal = 1.1E-28D)
    Sub M5(Optional x As Decimal = 1.1E-29D)
    Sub S1(Optional x As Decimal = -1E-25D)
    Sub S2(Optional x As Decimal = -1E-26D)
    Sub S3(Optional x As Decimal = -1E-27D)
    Sub S4(Optional x As Decimal = -1E-28D)
    Sub S5(Optional x As Decimal = -1E-29D)
    Sub T1(Optional x As Decimal = -1.1E-25D)
    Sub T2(Optional x As Decimal = -1.1E-26D)
    Sub T3(Optional x As Decimal = -1.1E-27D)
    Sub T4(Optional x As Decimal = -1.1E-28D)
    Sub T5(Optional x As Decimal = -1.1E-29D)
End Interface

Class C
    Implements [|I|]
End Class
</File>

            Dim expected =
<File>
Option Strict On

Interface I
    Sub F1(Optional x As Decimal = 1E-25D)
    Sub F2(Optional x As Decimal = 1E-26D)
    Sub F3(Optional x As Decimal = 1E-27D)
    Sub F4(Optional x As Decimal = 1E-28D)
    Sub F5(Optional x As Decimal = 1E-29D)
    Sub M1(Optional x As Decimal = 1.1E-25D)
    Sub M2(Optional x As Decimal = 1.1E-26D)
    Sub M3(Optional x As Decimal = 1.1E-27D)
    Sub M4(Optional x As Decimal = 1.1E-28D)
    Sub M5(Optional x As Decimal = 1.1E-29D)
    Sub S1(Optional x As Decimal = -1E-25D)
    Sub S2(Optional x As Decimal = -1E-26D)
    Sub S3(Optional x As Decimal = -1E-27D)
    Sub S4(Optional x As Decimal = -1E-28D)
    Sub S5(Optional x As Decimal = -1E-29D)
    Sub T1(Optional x As Decimal = -1.1E-25D)
    Sub T2(Optional x As Decimal = -1.1E-26D)
    Sub T3(Optional x As Decimal = -1.1E-27D)
    Sub T4(Optional x As Decimal = -1.1E-28D)
    Sub T5(Optional x As Decimal = -1.1E-29D)
End Interface

Class C
    Implements I

    Public Sub F1(Optional x As Decimal = 0.0000000000000000000000001D) Implements I.F1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub F2(Optional x As Decimal = 0.00000000000000000000000001D) Implements I.F2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub F3(Optional x As Decimal = 0.000000000000000000000000001D) Implements I.F3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub F4(Optional x As Decimal = 0.0000000000000000000000000001D) Implements I.F4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub F5(Optional x As Decimal = 0.0000000000000000000000000000D) Implements I.F5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M1(Optional x As Decimal = 0.00000000000000000000000011D) Implements I.M1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M2(Optional x As Decimal = 0.000000000000000000000000011D) Implements I.M2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M3(Optional x As Decimal = 0.0000000000000000000000000011D) Implements I.M3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M4(Optional x As Decimal = 0.0000000000000000000000000001D) Implements I.M4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub M5(Optional x As Decimal = 0.0000000000000000000000000000D) Implements I.M5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S1(Optional x As Decimal = -0.0000000000000000000000001D) Implements I.S1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S2(Optional x As Decimal = -0.00000000000000000000000001D) Implements I.S2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S3(Optional x As Decimal = -0.000000000000000000000000001D) Implements I.S3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S4(Optional x As Decimal = -0.0000000000000000000000000001D) Implements I.S4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub S5(Optional x As Decimal = 0.0000000000000000000000000000D) Implements I.S5
        Throw New System.NotImplementedException()
    End Sub

    Public Sub T1(Optional x As Decimal = -0.00000000000000000000000011D) Implements I.T1
        Throw New System.NotImplementedException()
    End Sub

    Public Sub T2(Optional x As Decimal = -0.000000000000000000000000011D) Implements I.T2
        Throw New System.NotImplementedException()
    End Sub

    Public Sub T3(Optional x As Decimal = -0.0000000000000000000000000011D) Implements I.T3
        Throw New System.NotImplementedException()
    End Sub

    Public Sub T4(Optional x As Decimal = -0.0000000000000000000000000001D) Implements I.T4
        Throw New System.NotImplementedException()
    End Sub

    Public Sub T5(Optional x As Decimal = 0.0000000000000000000000000000D) Implements I.T5
        Throw New System.NotImplementedException()
    End Sub
End Class
</File>

            Await TestAsync(markup, expected)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544641")>
        Public Async Function TestClassStatementTerminators1() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C : Implements [|IServiceProvider|] : End Class",
"Imports System
Class C : Implements IServiceProvider

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544641")>
        Public Async Function TestClassStatementTerminators2() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
MustInherit Class D
    MustOverride Sub Goo()
End Class
Class C : Inherits D : Implements [|IServiceProvider|] : End Class",
"Imports System
MustInherit Class D
    MustOverride Sub Goo()
End Class
Class C : Inherits D : Implements IServiceProvider

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544652"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestConvertNonprintableCharToString() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Object = CStr(Chr(1)))
End Interface

Class C
    Implements [|I|] ' Implement 
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As Object = CStr(Chr(1)))
End Interface

Class C
    Implements I ' Implement 

    Public Sub Goo(Optional x As Object = CStr(ChrW(1))) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545684"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestSimplifyModuleNameWhenPossible1() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(1))
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(1))
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As String = ChrW(1)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545684"), WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/715013")>
        Public Async Function TestSimplifyModuleNameWhenPossible2() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(1))
End Interface

Class C
    Implements [|I|]
    Public Sub ChrW(x As Integer)
    End Sub
End Class",
"Imports System
Imports Microsoft.VisualBasic
Interface I
    Sub Goo(Optional x As String = ChrW(1))
End Interface

Class C
    Implements I
    Public Sub ChrW(x As Integer)
    End Sub

    Public Sub Goo(Optional x As String = Strings.ChrW(1)) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/544676")>
        Public Async Function TestDoubleWideREM() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub ［ＲＥＭ］()
End Interface

Class C
    Implements [|I|]
End Class",
"Interface I
    Sub ［ＲＥＭ］()
End Interface

Class C
    Implements I

    Public Sub [ＲＥＭ]() Implements I.[ＲＥＭ]
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545917")>
        Public Async Function TestDoubleWideREM2() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Sub Goo(Of ［ＲＥＭ］)()
End Interface
Class C
    Implements [|I|]
End Class",
"Interface I
    Sub Goo(Of ［ＲＥＭ］)()
End Interface
Class C
    Implements I

    Public Sub Goo(Of [ＲＥＭ])() Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545953")>
        Public Async Function TestGenericEnumWithRenamedTypeParameters1() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Class C(Of T)
    Enum E
        X
    End Enum
End Class
Interface I
    Sub Goo(Of M)(Optional x As C(Of M()).E = C(Of M()).E.X)
End Interface
Class C
    Implements [|I|] ' Implement 
End Class",
"Option Strict On
Class C(Of T)
    Enum E
        X
    End Enum
End Class
Interface I
    Sub Goo(Of M)(Optional x As C(Of M()).E = C(Of M()).E.X)
End Interface
Class C
    Implements I ' Implement 

    Public Sub Goo(Of M)(Optional x As C(Of M()).E = C(Of M()).E.X) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/545953")>
        Public Async Function TestGenericEnumWithRenamedTypeParameters2() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Class C(Of T)
    Enum E
        X
    End Enum
End Class
Interface I
    Sub Goo(Of T)(Optional x As C(Of T()).E = C(Of T()).E.X)
End Interface
Class C
    Implements [|I|]
End Class",
"Option Strict On
Class C(Of T)
    Enum E
        X
    End Enum
End Class
Interface I
    Sub Goo(Of T)(Optional x As C(Of T()).E = C(Of T()).E.X)
End Interface
Class C
    Implements I

    Public Sub Goo(Of T)(Optional x As C(Of T()).E = C(Of T()).E.X) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546197")>
        Public Async Function TestDoubleQuoteChar() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Interface I
    Sub Goo(Optional x As Object = """"""""c)
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System
Interface I
    Sub Goo(Optional x As Object = """"""""c)
End Interface

Class C
    Implements I

    Public Sub Goo(Optional x As Object = """"""""c) Implements I.Goo
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530165")>
        Public Async Function TestGenerateIntoAppropriatePartial() As Task
            Await TestInRegularAndScriptAsync(
"Interface I

    Sub M()

End Interface

Class C

End Class

Partial Class C
    Implements [|I|]
End Class",
"Interface I

    Sub M()

End Interface

Class C

End Class

Partial Class C
    Implements I

    Public Sub M() Implements I.M
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546325")>
        Public Async Function TestAttributes() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Runtime.InteropServices

Interface I
    Function Goo(<MarshalAs(UnmanagedType.U1)> x As Boolean) As <MarshalAs(UnmanagedType.U1)> Boolean
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System.Runtime.InteropServices

Interface I
    Function Goo(<MarshalAs(UnmanagedType.U1)> x As Boolean) As <MarshalAs(UnmanagedType.U1)> Boolean
End Interface

Class C
    Implements I

    Public Function Goo(<MarshalAs(UnmanagedType.U1)> x As Boolean) As <MarshalAs(UnmanagedType.U1)> Boolean Implements I.Goo
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530564")>
        Public Async Function TestShortenedDecimal() As Task
            Await TestInRegularAndScriptAsync(
"Option Strict On
Interface I
    Sub Goo(Optional x As Decimal = 1000000000000000000D)
End Interface
Class C
    Implements [|I|] ' Implement 
End Class",
"Option Strict On
Interface I
    Sub Goo(Optional x As Decimal = 1000000000000000000D)
End Interface
Class C
    Implements I ' Implement 

    Public Sub Goo(Optional x As Decimal = 1000000000000000000) Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530713")>
        Public Async Function TestImplementAbstractly2() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    Property Goo() As Integer
End Interface

MustInherit Class C
    Implements [|I|] ' Implement interface abstractly 
End Class",
"Interface I
    Property Goo() As Integer
End Interface

MustInherit Class C
    Implements I ' Implement interface abstractly 

    Public Property Goo As Integer Implements I.Goo
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/916114")>
        Public Async Function TestOptionalNullableStructParameter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    ReadOnly Property g(Optional x As S? = Nothing)
End Interface
Class c
    Implements [|I|]
End Class
Structure S
End Structure",
"Interface I
    ReadOnly Property g(Optional x As S? = Nothing)
End Interface
Class c
    Implements I

    Public ReadOnly Property g(Optional x As S? = Nothing) As Object Implements I.g
        Get
            Throw New System.NotImplementedException()
        End Get
    End Property
End Class
Structure S
End Structure")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/916114")>
        Public Async Function TestOptionalNullableLongParameter() As Task
            Await TestInRegularAndScriptAsync(
"Interface I
    ReadOnly Property g(Optional x As Long? = Nothing, Optional y As Long? = 5)
End Interface
Class c
    Implements [|I|]
End Class",
"Interface I
    ReadOnly Property g(Optional x As Long? = Nothing, Optional y As Long? = 5)
End Interface
Class c
    Implements I

    Public ReadOnly Property g(Optional x As Long? = Nothing, Optional y As Long? = 5) As Object Implements I.g
        Get
            Throw New System.NotImplementedException()
        End Get
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/530345")>
        Public Async Function TestAttributeFormattingInNonStatementContext() As Task
            Await TestInRegularAndScriptAsync(
<Text>Imports System.Runtime.InteropServices

Interface I
    Function Goo(&lt;MarshalAs(UnmanagedType.U1)&gt; x As Boolean) As &lt;MarshalAs(UnmanagedType.U1)&gt; Boolean
End Interface

Class C
    Implements [|I|] ' Implement
End Class
</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Imports System.Runtime.InteropServices

Interface I
    Function Goo(&lt;MarshalAs(UnmanagedType.U1)&gt; x As Boolean) As &lt;MarshalAs(UnmanagedType.U1)&gt; Boolean
End Interface

Class C
    Implements I ' Implement

    Public Function Goo(&lt;MarshalAs(UnmanagedType.U1)&gt; x As Boolean) As &lt;MarshalAs(UnmanagedType.U1)&gt; Boolean Implements I.Goo
        Throw New System.NotImplementedException()
    End Function
End Class
</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/546779")>
        Public Async Function TestPropertyReturnTypeAttributes() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Runtime.InteropServices

Interface I
    Property P(<MarshalAs(UnmanagedType.I4)> x As Integer) As <MarshalAs(UnmanagedType.I4)> Integer
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System.Runtime.InteropServices

Interface I
    Property P(<MarshalAs(UnmanagedType.I4)> x As Integer) As <MarshalAs(UnmanagedType.I4)> Integer
End Interface

Class C
    Implements I

    Public Property P(<MarshalAs(UnmanagedType.I4)> x As Integer) As <MarshalAs(UnmanagedType.I4)> Integer Implements I.P
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/847464")>
        Public Async Function TestImplementInterfaceForPartialType() As Task
            Await TestInRegularAndScriptAsync(
"Public Interface I
    Sub Goo()
End Interface
Partial Class C
End Class
Partial Class C
    Implements [|I|]
End Class",
"Public Interface I
    Sub Goo()
End Interface
Partial Class C
End Class
Partial Class C
    Implements I

    Public Sub Goo() Implements I.Goo
        Throw New System.NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/617698")>
        Public Async Function TestBugfix_617698_RecursiveSimplificationOfQualifiedName() As Task
            Await TestInRegularAndScriptAsync(
<Text>Interface A(Of B)
    Sub M()
    Interface C(Of D)
        Inherits A(Of C(Of D))
        Interface E
            Inherits C(Of E)
            Class D
                Implements [|E|]
            End Class
        End Interface
    End Interface
End Interface
</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Interface A(Of B)
    Sub M()
    Interface C(Of D)
        Inherits A(Of C(Of D))
        Interface E
            Inherits C(Of E)
            Class D
                Implements E

                Public Sub M() Implements A(Of A(Of A(Of A(Of B).C(Of D)).C(Of A(Of B).C(Of D).E)).C(Of A(Of A(Of B).C(Of D)).C(Of A(Of B).C(Of D).E).E)).M
                    Throw New System.NotImplementedException()
                End Sub
            End Class
        End Interface
    End Interface
End Interface
</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceForIDisposable() As Task
            Await TestInRegularAndScriptAsync(
<Text>Imports System
Class Program
    Implements [|IDisposable|]

End Class
</Text>.Value.Replace(vbLf, vbCrLf),
$"Imports System
Class Program
    Implements IDisposable

    Private disposedValue As Boolean
{DisposePattern("Overridable ")}
End Class
",
index:=1)
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/9760")>
        Public Async Function TestImplementInterfaceForIDisposable_WithExistingDisposedValueField() As Task
            Await TestInRegularAndScriptAsync(
<Text>Imports System
Class Program
    Implements [|IDisposable|]

    Private disposedValue As Integer
End Class
</Text>.Value.Replace(vbLf, vbCrLf),
$"Imports System
Class Program
    Implements IDisposable

    Private disposedValue As Integer
    Private disposedValue1 As Boolean
{DisposePattern("Overridable ", disposeField:="disposedValue1")}
End Class
",
index:=1)
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceForIDisposableNonApplicable1() As Task
            Await TestInRegularAndScriptAsync(
<Text>Imports System
Class Program
    Implements [|IDisposable|]

    Private DisposedValue As Boolean

End Class
</Text>.Value.Replace(vbLf, vbCrLf),
<Text>Imports System
Class Program
    Implements IDisposable

    Private DisposedValue As Boolean

    Public Sub Dispose() Implements IDisposable.Dispose
        Throw New NotImplementedException()
    End Sub
End Class
</Text>.Value.Replace(vbLf, vbCrLf))
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceForIDisposableNonApplicable2() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class Program
    Implements [|IDisposable|]

    Public Sub Dispose(flag As Boolean)
    End Sub
End Class",
"Imports System
Class Program
    Implements IDisposable

    Public Sub Dispose(flag As Boolean)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact>
        Public Async Function TestImplementInterfaceForIDisposableWithSealedClass() As Task
            Await TestInRegularAndScriptAsync(
<Text>Imports System
Public NotInheritable Class Program
    Implements [|IDisposable|]

End Class
</Text>.Value.Replace(vbLf, vbCrLf),
$"Imports System
Public NotInheritable Class Program
    Implements IDisposable

    Private disposedValue As Boolean
{DisposePattern("", disposeMethodAccessibility:="Private")}
End Class
",
index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/939123")>
        Public Async Function TestNoComAliasNameAttributeOnMethodParameters() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Runtime.InteropServices

Interface I
    Function F(<ComAliasName(""pAlias"")> p As Long) As Integer
End Interface

MustInherit Class C
    Implements [|I|]
End Class",
"Imports System.Runtime.InteropServices

Interface I
    Function F(<ComAliasName(""pAlias"")> p As Long) As Integer
End Interface

MustInherit Class C
    Implements I

    Public Function F(p As Long) As Integer Implements I.F
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/939123")>
        Public Async Function TestNoComAliasNameAttributeOnMethodReturnType() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Runtime.InteropServices

Interface I
    Function F(<ComAliasName(""pAlias1"")> p As Long) As <ComAliasName(""pAlias2"")> Integer
End Interface

MustInherit Class C
    Implements [|I|]
End Class",
"Imports System.Runtime.InteropServices

Interface I
    Function F(<ComAliasName(""pAlias1"")> p As Long) As <ComAliasName(""pAlias2"")> Integer
End Interface

MustInherit Class C
    Implements I

    Public Function F(p As Long) As Integer Implements I.F
        Throw New System.NotImplementedException()
    End Function
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/939123")>
        Public Async Function TestNoComAliasNameAttributeOnPropertyParameters() As Task
            Await TestInRegularAndScriptAsync(
"Imports System.Runtime.InteropServices

Interface I
    Default Property Prop(<ComAliasName(""pAlias"")> p As Long) As Integer
End Interface

Class C
    Implements [|I|]
End Class",
"Imports System.Runtime.InteropServices

Interface I
    Default Property Prop(<ComAliasName(""pAlias"")> p As Long) As Integer
End Interface

Class C
    Implements I

    Default Public Property Prop(p As Long) As Integer Implements I.Prop
        Get
            Throw New System.NotImplementedException()
        End Get
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529920")>
        Public Async Function TestNewLineBeforeDirective() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C 
    Implements [|IServiceProvider|]
#Disable Warning",
"Imports System
Class C 
    Implements IServiceProvider

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class
#Disable Warning")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529947")>
        Public Async Function TestCommentAfterInterfaceList1() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C 
    Implements [|IServiceProvider|] REM Comment",
"Imports System
Class C 
    Implements IServiceProvider REM Comment

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class
")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/529947")>
        Public Async Function TestCommentAfterInterfaceList2() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C
    Implements [|IServiceProvider|]
REM Comment",
"Imports System
Class C
    Implements IServiceProvider

    Public Function GetService(serviceType As Type) As Object Implements IServiceProvider.GetService
        Throw New NotImplementedException()
    End Function
End Class
REM Comment")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994456")>
        <WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/958699")>
        Public Async Function TestImplementIDisposable_NoDisposePattern() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C : Implements [|IDisposable|]",
"Imports System
Class C : Implements IDisposable

    Public Sub Dispose() Implements IDisposable.Dispose
        Throw New NotImplementedException()
    End Sub
End Class
")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994456")>
        <WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/958699")>
        Public Async Function TestImplementIDisposable1_DisposePattern() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C : Implements [|IDisposable|]",
$"Imports System
Class C : Implements IDisposable

    Private disposedValue As Boolean
{DisposePattern("Overridable ")}
End Class
", index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994456")>
        <WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/958699")>
        Public Async Function TestImplementIDisposableAbstractly_NoDisposePattern() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
MustInherit Class C : Implements [|IDisposable|]",
"Imports System
MustInherit Class C : Implements IDisposable

    Public MustOverride Sub Dispose() Implements IDisposable.Dispose
End Class
", index:=2)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994456")>
        <WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/958699")>
        Public Async Function TestImplementIDisposableThroughMember_NoDisposePattern() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C : Implements [|IDisposable|]
    Dim goo As IDisposable
End Class",
"Imports System
Class C : Implements IDisposable
    Dim goo As IDisposable

    Public Sub Dispose() Implements IDisposable.Dispose
        goo.Dispose()
    End Sub
End Class", index:=2)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/941469")>
        Public Async Function TestImplementIDisposable2() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Class C : Implements [|System.IDisposable|]
    Class IDisposable
    End Class
End Class",
$"Imports System
Class C : Implements System.IDisposable

    Private disposedValue As Boolean

    Class IDisposable
    End Class
{DisposePattern("Overridable ", simplifySystem:=False)}
End Class", index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/958699")>
        Public Async Function TestImplementIDisposable_NoNamespaceImportForSystem() As Task
            Await TestInRegularAndScriptAsync(
"Class C : Implements [|System.IDisposable|]
",
$"Class C : Implements System.IDisposable

    Private disposedValue As Boolean
{DisposePattern("Overridable ", simplifySystem:=False, gcPrefix:="System.")}
End Class
", index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/951968")>
        Public Async Function TestImplementIDisposableViaBaseInterface_NoDisposePattern() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Interface I : Inherits IDisposable
    Sub F()
End Interface
Class C : Implements [|I|]
End Class",
"Imports System
Interface I : Inherits IDisposable
    Sub F()
End Interface
Class C : Implements I

    Public Sub F() Implements I.F
        Throw New NotImplementedException()
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Throw New NotImplementedException()
    End Sub
End Class")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/951968")>
        Public Async Function TestImplementIDisposableViaBaseInterface() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Interface I : Inherits IDisposable
    Sub F()
End Interface
Class C : Implements [|I|]
End Class",
$"Imports System
Interface I : Inherits IDisposable
    Sub F()
End Interface
Class C : Implements I

    Private disposedValue As Boolean

    Public Sub F() Implements I.F
        Throw New NotImplementedException()
    End Sub
{DisposePattern("Overridable ")}
End Class", index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/951968")>
        Public Async Function TestDoNotImplementDisposePatternForLocallyDefinedIDisposable() As Task
            Await TestInRegularAndScriptAsync(
"Namespace System
    Interface IDisposable
        Sub Dispose
    End Interface

    Class C : Implements [|IDisposable|]
End Namespace",
"Namespace System
    Interface IDisposable
        Sub Dispose
    End Interface

    Class C : Implements IDisposable

        Public Sub Dispose() Implements IDisposable.Dispose
            Throw New NotImplementedException()
        End Sub
    End Class
End Namespace")
        End Function

        <Fact>
        Public Async Function TestDoNotImplementDisposePatternForStructures() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Structure S : Implements [|IDisposable|]",
"Imports System
Structure S : Implements IDisposable

    Public Sub Dispose() Implements IDisposable.Dispose
        Throw New NotImplementedException()
    End Sub
End Structure
")
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994328")>
        Public Async Function TestDisposePatternWhenAdditionalImportsAreIntroduced1() As Task
            Await TestInRegularAndScriptAsync(
$"Interface I(Of T, U As T) : Inherits System.IDisposable, System.IEquatable(Of Integer)
    Function M(a As System.Collections.Generic.Dictionary(Of T, System.Collections.Generic.List(Of U)), b As T, c As U) As System.Collections.Generic.List(Of U)
    Function M(Of TT, UU As TT)(a As System.Collections.Generic.Dictionary(Of TT, System.Collections.Generic.List(Of UU)), b As TT, c As UU) As System.Collections.Generic.List(Of UU)
End Interface

Class _
    C
    Implements [|I(Of System.Exception, System.AggregateException)|]
End Class

Partial Class C
    Implements System.IDisposable
End Class",
$"Imports System
Imports System.Collections.Generic

Interface I(Of T, U As T) : Inherits System.IDisposable, System.IEquatable(Of Integer)
    Function M(a As System.Collections.Generic.Dictionary(Of T, System.Collections.Generic.List(Of U)), b As T, c As U) As System.Collections.Generic.List(Of U)
    Function M(Of TT, UU As TT)(a As System.Collections.Generic.Dictionary(Of TT, System.Collections.Generic.List(Of UU)), b As TT, c As UU) As System.Collections.Generic.List(Of UU)
End Interface

Class _
    C
    Implements I(Of System.Exception, System.AggregateException)

    Private disposedValue As Boolean

    Public Function M(a As Dictionary(Of Exception, List(Of AggregateException)), b As Exception, c As AggregateException) As List(Of AggregateException) Implements I(Of Exception, AggregateException).M
        Throw New NotImplementedException()
    End Function

    Public Function M(Of TT, UU As TT)(a As Dictionary(Of TT, List(Of UU)), b As TT, c As UU) As List(Of UU) Implements I(Of Exception, AggregateException).M
        Throw New NotImplementedException()
    End Function

    Public Function Equals(other As Integer) As Boolean Implements IEquatable(Of Integer).Equals
        Throw New NotImplementedException()
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' { CodeFixesResources.TODO_colon_dispose_managed_state_managed_objects }
            End If

            ' { CodeFixesResources.TODO_colon_free_unmanaged_resources_unmanaged_objects_and_override_finalizer}
            ' { CodeFixesResources.TODO_colon_set_large_fields_to_null }
            disposedValue = True
        End If
    End Sub

    ' ' { String.Format(CodeFixesResources.TODO_colon_override_finalizer_only_if_0_has_code_to_free_unmanaged_resources, "Dispose(disposing As Boolean)") }
    ' Protected Overrides Sub Finalize()
    '     ' { String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)") }
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' { String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)") }
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class

Partial Class C
    Implements System.IDisposable
End Class",
 index:=1)
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/994328")>
        Public Async Function TestDisposePatternWhenAdditionalImportsAreIntroduced2() As Task
            Await TestInRegularAndScriptAsync(
$"Class C
End Class

Partial Class C
    Implements [|I(Of System.Exception, System.AggregateException)|]
    Implements System.IDisposable
End Class

Interface I(Of T, U As T) : Inherits System.IDisposable, System.IEquatable(Of Integer)
    Function M(a As System.Collections.Generic.Dictionary(Of T, System.Collections.Generic.List(Of U)), b As T, c As U) As System.Collections.Generic.List(Of U)
    Function M(Of TT, UU As TT)(a As System.Collections.Generic.Dictionary(Of TT, System.Collections.Generic.List(Of UU)), b As TT, c As UU) As System.Collections.Generic.List(Of UU)
End Interface",
$"Imports System
Imports System.Collections.Generic

Class C
End Class

Partial Class C
    Implements I(Of System.Exception, System.AggregateException)
    Implements System.IDisposable

    Private disposedValue As Boolean

    Public Function M(a As Dictionary(Of Exception, List(Of AggregateException)), b As Exception, c As AggregateException) As List(Of AggregateException) Implements I(Of Exception, AggregateException).M
        Throw New NotImplementedException()
    End Function

    Public Function M(Of TT, UU As TT)(a As Dictionary(Of TT, List(Of UU)), b As TT, c As UU) As List(Of UU) Implements I(Of Exception, AggregateException).M
        Throw New NotImplementedException()
    End Function

    Public Function Equals(other As Integer) As Boolean Implements IEquatable(Of Integer).Equals
        Throw New NotImplementedException()
    End Function

    Protected Overridable Sub Dispose(disposing As Boolean)
        If Not disposedValue Then
            If disposing Then
                ' { CodeFixesResources.TODO_colon_dispose_managed_state_managed_objects }
            End If

            ' { CodeFixesResources.TODO_colon_free_unmanaged_resources_unmanaged_objects_and_override_finalizer }
            ' { CodeFixesResources.TODO_colon_set_large_fields_to_null }
            disposedValue = True
        End If
    End Sub

    ' ' { String.Format(CodeFixesResources.TODO_colon_override_finalizer_only_if_0_has_code_to_free_unmanaged_resources, "Dispose(disposing As Boolean)") }
    ' Protected Overrides Sub Finalize()
    '     ' { String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)")}
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        ' { String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)") }
        Dispose(disposing:=True)
        GC.SuppressFinalize(Me)
    End Sub
End Class

Interface I(Of T, U As T) : Inherits System.IDisposable, System.IEquatable(Of Integer)
    Function M(a As System.Collections.Generic.Dictionary(Of T, System.Collections.Generic.List(Of U)), b As T, c As U) As System.Collections.Generic.List(Of U)
    Function M(Of TT, UU As TT)(a As System.Collections.Generic.Dictionary(Of TT, System.Collections.Generic.List(Of UU)), b As TT, c As UU) As System.Collections.Generic.List(Of UU)
End Interface",
 index:=1)
        End Function

        Private Shared Function DisposePattern(
                disposeMethodModifiers As String,
                Optional simplifySystem As Boolean = True,
                Optional disposeField As String = "disposedValue",
                Optional gcPrefix As String = "",
                Optional disposeMethodAccessibility As String = "Protected") As String
            Dim code = $"
    {disposeMethodAccessibility} {disposeMethodModifiers}Sub Dispose(disposing As Boolean)
        If Not {disposeField} Then
            If disposing Then
                ' {CodeFixesResources.TODO_colon_dispose_managed_state_managed_objects}
            End If

            ' {CodeFixesResources.TODO_colon_free_unmanaged_resources_unmanaged_objects_and_override_finalizer}
            ' {CodeFixesResources.TODO_colon_set_large_fields_to_null}
            {disposeField} = True
        End If
    End Sub

    ' ' {String.Format(CodeFixesResources.TODO_colon_override_finalizer_only_if_0_has_code_to_free_unmanaged_resources, "Dispose(disposing As Boolean)")}
    ' Protected Overrides Sub Finalize()
    '     ' {String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)")}
    '     Dispose(disposing:=False)
    '     MyBase.Finalize()
    ' End Sub

    Public Sub Dispose() Implements System.IDisposable.Dispose
        ' {String.Format(CodeFixesResources.Do_not_change_this_code_Put_cleanup_code_in_0_method, "Dispose(disposing As Boolean)")}
        Dispose(disposing:=True)
        {gcPrefix}GC.SuppressFinalize(Me)
    End Sub"

            ' some tests count on "System." being simplified out
            If simplifySystem Then
                code = code.Replace("System.IDisposable.Dispose", "IDisposable.Dispose")
            End If

            Return code
        End Function

        <Fact, WorkItem("http://vstfdevdiv:8080/DevDiv2/DevDiv/_workitems/edit/1132014")>
        Public Async Function TestInaccessibleAttributes() As Task
            Await TestInRegularAndScriptAsync(
"Imports System

Public Class Goo
    Implements [|Holder.SomeInterface|]
End Class

Public Class Holder
	Public Interface SomeInterface
		Sub Something(<SomeAttribute> helloWorld As String)
	End Interface

	Private Class SomeAttribute
		Inherits Attribute
	End Class
End Class",
"Imports System

Public Class Goo
    Implements Holder.SomeInterface

    Public Sub Something(helloWorld As String) Implements Holder.SomeInterface.Something
        Throw New NotImplementedException()
    End Sub
End Class

Public Class Holder
	Public Interface SomeInterface
		Sub Something(<SomeAttribute> helloWorld As String)
	End Interface

	Private Class SomeAttribute
		Inherits Attribute
	End Class
End Class")
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/2785")>
        Public Async Function TestImplementInterfaceThroughStaticMemberInGenericClass() As Task
            Await TestInRegularAndScriptAsync(
"Imports System
Imports System.Collections.Generic
Class Program(Of T)
    Implements [|IList(Of Object)|]
    Private Shared innerList As List(Of Object) = New List(Of Object)
End Class",
"Imports System
Imports System.Collections
Imports System.Collections.Generic
Class Program(Of T)
    Implements IList(Of Object)
    Private Shared innerList As List(Of Object) = New List(Of Object)

    Default Public Property Item(index As Integer) As Object Implements IList(Of Object).Item
        Get
            Return DirectCast(innerList, IList(Of Object))(index)
        End Get
        Set(value As Object)
            DirectCast(innerList, IList(Of Object))(index) = value
        End Set
    End Property

    Public ReadOnly Property Count As Integer Implements ICollection(Of Object).Count
        Get
            Return DirectCast(innerList, ICollection(Of Object)).Count
        End Get
    End Property

    Public ReadOnly Property IsReadOnly As Boolean Implements ICollection(Of Object).IsReadOnly
        Get
            Return DirectCast(innerList, ICollection(Of Object)).IsReadOnly
        End Get
    End Property

    Public Sub Insert(index As Integer, item As Object) Implements IList(Of Object).Insert
        DirectCast(innerList, IList(Of Object)).Insert(index, item)
    End Sub

    Public Sub RemoveAt(index As Integer) Implements IList(Of Object).RemoveAt
        DirectCast(innerList, IList(Of Object)).RemoveAt(index)
    End Sub

    Public Sub Add(item As Object) Implements ICollection(Of Object).Add
        DirectCast(innerList, ICollection(Of Object)).Add(item)
    End Sub

    Public Sub Clear() Implements ICollection(Of Object).Clear
        DirectCast(innerList, ICollection(Of Object)).Clear()
    End Sub

    Public Sub CopyTo(array() As Object, arrayIndex As Integer) Implements ICollection(Of Object).CopyTo
        DirectCast(innerList, ICollection(Of Object)).CopyTo(array, arrayIndex)
    End Sub

    Public Function IndexOf(item As Object) As Integer Implements IList(Of Object).IndexOf
        Return DirectCast(innerList, IList(Of Object)).IndexOf(item)
    End Function

    Public Function Contains(item As Object) As Boolean Implements ICollection(Of Object).Contains
        Return DirectCast(innerList, ICollection(Of Object)).Contains(item)
    End Function

    Public Function Remove(item As Object) As Boolean Implements ICollection(Of Object).Remove
        Return DirectCast(innerList, ICollection(Of Object)).Remove(item)
    End Function

    Public Function GetEnumerator() As IEnumerator(Of Object) Implements IEnumerable(Of Object).GetEnumerator
        Return DirectCast(innerList, IEnumerable(Of Object)).GetEnumerator()
    End Function

    Private Function IEnumerable_GetEnumerator() As IEnumerator Implements IEnumerable.GetEnumerator
        Return DirectCast(innerList, IEnumerable).GetEnumerator()
    End Function
End Class",
index:=1)
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/11444")>
        Public Async Function TestAbstractConflictingMethod() As Task
            Await TestInRegularAndScriptAsync(
"Friend Interface IFace
    Sub M()
End Interface

Public MustInherit Class C
    Implements [|IFace|]

    Public MustOverride Sub M()
End Class",
"Friend Interface IFace
    Sub M()
End Interface

Public MustInherit Class C
    Implements IFace

    Public MustOverride Sub M()
    Public MustOverride Sub IFace_M() Implements IFace.M
End Class",
index:=1)
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/16793")>
        Public Async Function TestMethodWithValueTupleArity1() As Task
            Await TestInRegularAndScriptAsync(
"
Imports System
interface I
    Function F() As ValueTuple(Of Object)
end interface
class C
    Implements [|I|]

end class
Namespace System
    Structure ValueTuple(Of T1)
    End Structure
End Namespace",
"
Imports System
interface I
    Function F() As ValueTuple(Of Object)
end interface
class C
    Implements I

    Public Function F() As ValueTuple(Of Object) Implements I.F
        Throw New NotImplementedException()
    End Function
end class
Namespace System
    Structure ValueTuple(Of T1)
    End Structure
End Namespace")
        End Function

        <Fact, WorkItem("https://github.com/dotnet/roslyn/issues/13932")>
        <WorkItem("https://github.com/dotnet/roslyn/issues/5898")>
        Public Async Function TestAutoProperties() As Task
            Await TestInRegularAndScriptAsync(
"interface IInterface
    readonly property ReadOnlyProp as integer
    property ReadWriteProp as integer
    writeonly property WriteOnlyProp as integer
end interface

class Class
    implements [|IInterface|]
end class",
"interface IInterface
    readonly property ReadOnlyProp as integer
    property ReadWriteProp as integer
    writeonly property WriteOnlyProp as integer
end interface

class Class
    implements IInterface

    Public ReadOnly Property ReadOnlyProp As Integer Implements IInterface.ReadOnlyProp
    Public Property ReadWriteProp As Integer Implements IInterface.ReadWriteProp

    Public WriteOnly Property WriteOnlyProp As Integer Implements IInterface.WriteOnlyProp
        Set(value As Integer)
            Throw New System.NotImplementedException()
        End Set
    End Property
end class", parameters:=New TestParameters(options:=[Option](ImplementTypeOptionsStorage.PropertyGenerationBehavior, ImplementTypePropertyGenerationBehavior.PreferAutoProperties)))
        End Function
    End Class
End Namespace
