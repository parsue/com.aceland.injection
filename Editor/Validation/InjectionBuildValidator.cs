using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AceLand.Injection.Editor
{
    public sealed class InjectionBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var s = InjectionValidationSettings.instance;
            if (!s.validateOnBuild) return;

            var result = InjectionValidator.ValidateAll();
            foreach (var i in result.Issues.Where(i => i.Severity != IssueSeverity.Info))
            {
                if (i.Severity == IssueSeverity.Error) Debug.LogError("[Injection] " + i);
                else Debug.LogWarning("[Injection] " + i);
            }

            if (result.ErrorCount > 0 && s.failBuildOnError)
                throw new BuildFailedException(
                    $"AceLand Injection validation failed with {result.ErrorCount} error(s). " +
                    "See Console, or Window ▸ AceLand ▸ Injection ▸ Validation.");
        }
    }
}