using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AceLand.Injection.Editor.Graph
{
    internal enum NodeKind { Scope, Registration, Consumer, Unresolved }

    internal enum EdgeKind
    {
        ScopeParent,     // scope → parent scope
        Provides,        // scope → registration
        Resolves,        // consumer → registration
        Deferred,        // via Func<T> / Lazy<T>
        Collection,      // via IEnumerable<T> — fan-out
        Component,       // [Self]/[Parent]/[Child] — hierarchy, not container
        Missing          // consumer → unresolved
    }
    
    internal sealed class GraphGroup
    {
        public string Id;
        public string Title;
        public string Subtitle;
        public int ColorIndex;
        public int Depth;                    // NEW — scope depth, drives the column
        public int Column;                   // NEW — assigned by layout
        public bool IsError;
        public bool IsWarning;               // NEW
        public readonly List<string> Notes = new();   // NEW
        public Rect Rect;
        public Rect HeaderRect;              // NEW — world space, for hit testing
        public readonly List<GraphNode> Nodes = new();

        public string ScenePath, ObjectPath, ComponentTypeName;
        public UnityEngine.Object Target;
        
        public Type ResolvedType;
        public string TypeFullName;
        public string Origin;

        public bool IsScope => Id != "group:unresolved" && Id != "group:consumers";
    }

    internal sealed class GraphNode
    {
        public string Id;
        public NodeKind Kind;
        public string Title;
        public string Subtitle;
        public readonly List<string> Details = new();
        public bool HasError;
        public bool IsInstantiated;
        public int Depth;
        public string OwnerScopeId;
        public UnityEngine.Object Target;      // for Select / ping
        public Rect Rect;          // filled by layout
        public string ScenePath;
        public string ObjectPath;
        public string ComponentTypeName;
        public string Namespace;
        public string StateLabel;            // "Singleton" / "Scoped · live"
        public readonly List<string> Contracts = new();
        public Type ResolvedType;          // for origin + exact script lookup
        public string TypeFullName;
        public string Origin;              // "com.aceland.library 2.2.3"
    }

    internal sealed class GraphEdge
    {
        public string FromId;
        public string ToId;
        public EdgeKind Kind;
        public string Label;
    }

    internal sealed class InjectionGraph
    {
        public string Context = "";                                    // scene path or "Runtime"
        public readonly List<GraphNode> Nodes = new();
        public readonly List<GraphEdge> Edges = new();
        public readonly List<GraphGroup> Groups = new();

        private readonly Dictionary<string, GraphNode> _byId = new();
        private readonly Dictionary<string, GraphGroup> _groupsById = new();

        private readonly Dictionary<IObjectResolver, string> _scopeIds = new Dictionary<IObjectResolver, string>();
        private readonly Dictionary<int, string> _regIdBySerial = new Dictionary<int, string>();
        
        public int WarningCount => Groups.Count(g => g.IsWarning);
        public int IssueCount   => ErrorCount + WarningCount;
        
        /// <summary>
        /// Stable scope id. GameObject InstanceID survives rescans; container hash codes do not.
        /// </summary>
        public string ScopeIdFor(IObjectResolver resolver, LifetimeScope source)
        {
            if (resolver == null) return null;
            if (_scopeIds.TryGetValue(resolver, out var existing)) return existing;

            var id = source != null
#if UNITY_6000_4_OR_NEWER
                ? "scope:obj:" + source.GetEntityId()
#else
                ? "scope:obj:" + source.GetInstanceID()
#endif
                : "scope:global";
            _scopeIds[resolver] = id;
            return id;
        }

        /// <summary>Id of an already-registered scope, or null.</summary>
        public string ExistingScopeId(IObjectResolver resolver)
            => resolver != null && _scopeIds.TryGetValue(resolver, out var id) ? id : null;

        /// <summary>
        /// Content-derived registration id. <paramref name="used"/> is a per-scope counter
        /// that disambiguates duplicate contract+impl pairs.
        /// </summary>
        public string RegistrationId(string scopeId, RegistrationInfo reg, Dictionary<string, int> used)
        {
            var impl = reg.ImplementationType?.FullName ?? "?";
            var contract = reg.ContractTypes != null && reg.ContractTypes.Length > 0
                ? reg.ContractTypes[0].FullName
                : impl;

            var key = $"reg:{scopeId}|{contract}|{impl}" + (reg.Id != null ? "#" + reg.Id : "");

            if (used.TryGetValue(key, out var seen))
            {
                used[key] = seen + 1;
                key += "~" + (seen + 1);
            }
            else used[key] = 0;

            _regIdBySerial[reg.Serial] = key;      // serials are unique *within* one scan
            return key;
        }

        /// <summary>Look up an id assigned earlier in this scan.</summary>
        public string RegistrationIdBySerial(int serial)
            => _regIdBySerial.TryGetValue(serial, out var id) ? id : null;

        // keep these — already content-based
        public static string ConsumerId(Type t, string path) => "use:" + t.FullName + "@" + path;
        public static string MissingId(Type contract, object id)
            => "miss:" + contract.FullName + (id != null ? "#" + id : "");
        
        public GraphGroup ParentGroupOf(GraphGroup group)
        {
            var edge = Edges.FirstOrDefault(e => e.Kind == EdgeKind.ScopeParent && e.FromId == group.Id);
            return edge != null ? FindGroup(edge.ToId) : null;
        }
        
        public GraphGroup AddGroup(GraphGroup group)
        {
            if (_groupsById.TryGetValue(group.Id, out var existing)) return existing;
            _groupsById[group.Id] = group;
            Groups.Add(group);
            return group;
        }

        public GraphNode Add(GraphNode node)
        {
            if (_byId.TryGetValue(node.Id, out var existing)) return existing;
            _byId[node.Id] = node;
            Nodes.Add(node);
            return node;
        }

        public GraphNode Find(string id) => _byId.GetValueOrDefault(id);
        
        public GraphGroup FindGroup(string id) => _groupsById.GetValueOrDefault(id);

        public GraphGroup GroupOf(GraphNode node) => node?.OwnerScopeId != null ? FindGroup(node.OwnerScopeId) : null;

        /// <summary>What this node needs (outgoing resolve edges).</summary>
        public IEnumerable<GraphNode> DependenciesOf(string nodeId)
            => Edges.Where(e => e.FromId == nodeId && e.Kind != EdgeKind.ScopeParent &&
                                e.Kind != EdgeKind.Provides)
                .Select(e => Find(e.ToId)).Where(n => n != null);

        public void Connect(string from, string to, EdgeKind kind, string label = null)
        {
            if (from == null || to == null) return;
            Edges.Add(new GraphEdge { FromId = from, ToId = to, Kind = kind, Label = label });
        }

        public int ErrorCount => Nodes.Count(n => n.HasError);

        public IEnumerable<GraphNode> OfKind(NodeKind kind) => Nodes.Where(n => n.Kind == kind);

        public IEnumerable<GraphNode> ConsumersOf(string registrationId)
            => Edges.Where(e => e.ToId == registrationId &&
                                (e.Kind == EdgeKind.Resolves || e.Kind == EdgeKind.Deferred))
                    .Select(e => Find(e.FromId))
                    .Where(n => n != null);
    }
}