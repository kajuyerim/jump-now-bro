using System;
using System.Collections.Generic;

namespace JumpNowBro.Util
{
    /// Engine-free conflict check for control rebinding (#135), CI-tested. The caller flattens the
    /// candidate map's bindings for one device group into (action, effectivePath) pairs — INCLUDING
    /// composite parts that have no UI cell (rebinding onto W would silently double-map inside the
    /// move 2DVector otherwise) — and asks whether a candidate path collides with another action.
    public static class BindingConflicts
    {
        /// Returns the name of a DIFFERENT action already using candidatePath, or null when free.
        /// Same-action matches are not conflicts (re-binding to the current key, or an action's own
        /// alias cell). Paths compare case-insensitively (control paths are case-stable but cheap to
        /// be safe about).
        public static string FindConflict(IReadOnlyList<(string action, string path)> bindings,
                                          string forAction, string candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath)) return null;
            for (int i = 0; i < bindings.Count; i++)
            {
                var (action, path) = bindings[i];
                if (string.Equals(action, forAction, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(path, candidatePath, StringComparison.OrdinalIgnoreCase)) return action;
            }
            return null;
        }
    }
}
