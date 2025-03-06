// Port convention (no C++ file): every C++ unit test is its own program, so it starts with an empty string table and
// the default rounding mode. The C# tests share one process; this attribute restores that start state before each test.
using System.Reflection;
using OpenTribes.Core;
using Xunit.Sdk;

[assembly: OpenTribes.Tests.ProcessState]

namespace OpenTribes.Tests;

[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ProcessStateAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest)
    {
        StringTable.ResetGlobal();
        Fpu.TowardZero = false;
        OpenTribes.Game.StaticRandom.Reset();
        OpenTribes.Game.ShapeState.Reset();
    }
}
