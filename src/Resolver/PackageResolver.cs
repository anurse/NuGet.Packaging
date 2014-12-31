﻿using NuGet.Packaging;
using NuGet.PackagingCore;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuGet.Resolver
{
    /// <summary>
    /// A core package dependency resolver.
    /// </summary>
    /// <remarks>Not thread safe (yet)</remarks>
    public class PackageResolver : IPackageResolver
    {
        private DependencyBehavior _dependencyBehavior;
        private HashSet<PackageIdentity> _installedPackages;

        /// <summary>
        /// Core package resolver
        /// </summary>
        /// <param name="dependencyBehavior">Dependency version behavior</param>
        public PackageResolver(DependencyBehavior dependencyBehavior)
        {
            _dependencyBehavior = dependencyBehavior;
        }

        public IEnumerable<PackageIdentity> Resolve(IEnumerable<PackageIdentity> targets, IEnumerable<PackageDependencyInfo> availablePackages)
        {
            return Resolve(targets, availablePackages, null);
        }

        public IEnumerable<PackageIdentity> Resolve(IEnumerable<PackageIdentity> targets, IEnumerable<PackageDependencyInfo> availablePackages, IEnumerable<PackageReference> installedPackages)
        {
            _installedPackages = new HashSet<PackageIdentity>(installedPackages.Select(e => e.PackageIdentity), PackageIdentity.Comparer);

            var solver = new CombinationSolver<ResolverPackage>();

            CompareWrapper<ResolverPackage> comparer = new CompareWrapper<ResolverPackage>(Compare);

            List<List<ResolverPackage>> grouped = new List<List<ResolverPackage>>();

            var packageComparer = PackageIdentity.Comparer;

            var resolverPackages = availablePackages.Select(e => new ResolverPackage(e.Id, e.Version));

            foreach (var group in resolverPackages.GroupBy(e => e.Id))
            {
                List<ResolverPackage> curSet = group.Select(e => e).ToList();

                // add an absent package for non-targets
                // being absent allows the resolver to throw it out if it is not needed
                if (!targets.Any(e => StringComparer.OrdinalIgnoreCase.Equals(e.Id, group.Key)))
                {
                    curSet.Add(new ResolverPackage(group.Key, null, null, true));
                }

                grouped.Add(curSet);
            }

            var solution = solver.FindSolution(grouped, comparer, ShouldRejectPackagePair);

            var nonAbsentCandidates = solution.Where(c => !c.Absent);

            var sortedSolution = TopologicalSort(nonAbsentCandidates);

            return sortedSolution.ToArray();
        }

        private IEnumerable<ResolverPackage> TopologicalSort(IEnumerable<ResolverPackage> nodes)
        {
            List<ResolverPackage> result = new List<ResolverPackage>();

            var dependsOn = new Func<ResolverPackage, ResolverPackage, bool>((x, y) =>
            {
                return x.FindDependencyRange(y.Id) != null;
            });

            var dependenciesAreSatisfied = new Func<ResolverPackage, bool>(node =>
            {
                var dependencies = node.Dependencies;
                return dependencies == null || !dependencies.Any() ||
                       dependencies.All(d => result.Any(r => StringComparer.OrdinalIgnoreCase.Equals(r.Id, d.Id)));
            });

            var satisfiedNodes = new HashSet<ResolverPackage>(nodes.Where(n => dependenciesAreSatisfied(n)));
            while (satisfiedNodes.Any())
            {
                //Pick any element from the set. Remove it, and add it to the result list.
                var node = satisfiedNodes.First();
                satisfiedNodes.Remove(node);
                result.Add(node);

                // Find unprocessed nodes that depended on the node we just added to the result.
                // If all of its dependencies are now satisfied, add it to the set of nodes to process.
                var newlySatisfiedNodes = nodes.Except(result)
                                               .Where(n => dependsOn(n, node))
                                               .Where(n => dependenciesAreSatisfied(n));

                foreach (var cur in newlySatisfiedNodes)
                {
                    satisfiedNodes.Add(cur);
                }
            }

            return result;
        }

        private int Compare(ResolverPackage x, ResolverPackage y)
        {
            Debug.Assert(string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase));

            // The absent package comes first in the sort order
            bool isXAbsent = x.Absent;
            bool isYAbsent = y.Absent;
            if (isXAbsent && !isYAbsent)
            {
                return -1;
            }
            if (!isXAbsent && isYAbsent)
            {
                return 1;
            }
            if (isXAbsent && isYAbsent)
            {
                return 0;
            }

            if (_installedPackages != null)
            {
                //Already installed packages come next in the sort order.
                bool xInstalled = _installedPackages.Contains(x);
                bool yInstalled = _installedPackages.Contains(y);
                if (xInstalled && !yInstalled)
                {
                    return -1;
                }

                if (!xInstalled && yInstalled)
                {
                    return 1;
                }
            }

            var xv = x.Version;
            var yv = y.Version;

            switch (_dependencyBehavior)
            {
                case DependencyBehavior.Lowest:
                    return VersionComparer.Default.Compare(xv, yv);
                case DependencyBehavior.Highest:
                    return -1 * VersionComparer.Default.Compare(xv, yv);
                case DependencyBehavior.HighestMinor:
                    {
                        if (VersionComparer.Default.Equals(xv, yv)) return 0;

                        //TODO: This is surely wrong...
                        return new[] { x, y }.OrderBy(p => p.Version.Major)
                                           .ThenByDescending(p => p.Version.Minor)
                                           .ThenByDescending(p => p.Version.Patch).FirstOrDefault() == x ? -1 : 1;

                    }
                case DependencyBehavior.HighestPatch:
                    {
                        if (VersionComparer.Default.Equals(xv, yv)) return 0;

                        //TODO: This is surely wrong...
                        return new[] { x, y }.OrderBy(p => p.Version.Major)
                                             .ThenBy(p => p.Version.Minor)
                                             .ThenByDescending(p => p.Version.Patch).FirstOrDefault() == x ? -1 : 1;
                    }
                default:
                    throw new InvalidOperationException("Unknown DependencyBehavior value.");
            }
        }

        private static bool ShouldRejectPackagePair(ResolverPackage p1, ResolverPackage p2)
        {
            var p1ToP2Dependency = p1.FindDependencyRange(p2.Id);
            if (p1ToP2Dependency != null)
            {
                return p2.Absent || !p1ToP2Dependency.Satisfies(p2.Version);
            }

            var p2ToP1Dependency = p2.FindDependencyRange(p1.Id);
            if (p2ToP1Dependency != null)
            {
                return p1.Absent || !p2ToP1Dependency.Satisfies(p1.Version);
            }

            return false;
        }
    }
}
