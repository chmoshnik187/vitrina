using Vitrines.Core.Models;

namespace Vitrines.Core.Rules;

public interface IRuleEngine
{
    /// <summary>Оценить элемент по упорядоченным правилам и вернуть решение с объяснением.</summary>
    RuleDecision Evaluate(string path, ItemKind kind, IEnumerable<SortingRule> rules);

    /// <summary>Dry-run: решения для набора путей без применения действий.</summary>
    IReadOnlyList<(string Path, RuleDecision Decision)> DryRun(IEnumerable<string> paths, IEnumerable<SortingRule> rules);
}
