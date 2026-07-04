using System.Collections.Generic;
using NUnit.Framework;
using JumpNowBro.Util;

namespace JumpNowBro.Tests
{
    public class BindingConflictsTests
    {
        static readonly List<(string action, string path)> Bindings = new List<(string, string)>
        {
            ("Move", "<Keyboard>/a"),
            ("Move", "<Keyboard>/d"),
            ("Move", "<Keyboard>/leftArrow"),
            ("Jump", "<Keyboard>/space"),
            ("Dash", "<Keyboard>/leftShift"),
            ("Dash", "<Keyboard>/rightShift"),
        };

        [Test]
        public void SamePathOnOtherAction_ReportsThatAction()
        {
            Assert.AreEqual("Move", BindingConflicts.FindConflict(Bindings, "Jump", "<Keyboard>/a"));
            Assert.AreEqual("Dash", BindingConflicts.FindConflict(Bindings, "Jump", "<Keyboard>/rightShift"));
        }

        [Test]
        public void SamePathOnSameAction_NotAConflict()
        {
            // Re-binding to the current key, or landing on the action's own alias binding.
            Assert.IsNull(BindingConflicts.FindConflict(Bindings, "Dash", "<Keyboard>/leftShift"));
            Assert.IsNull(BindingConflicts.FindConflict(Bindings, "Move", "<Keyboard>/leftArrow"));
        }

        [Test]
        public void DifferentPath_NoConflict()
        {
            Assert.IsNull(BindingConflicts.FindConflict(Bindings, "Jump", "<Keyboard>/k"));
        }

        [Test]
        public void Comparison_IgnoresCase()
        {
            Assert.AreEqual("Jump", BindingConflicts.FindConflict(Bindings, "Dash", "<Keyboard>/SPACE"));
        }
    }
}
