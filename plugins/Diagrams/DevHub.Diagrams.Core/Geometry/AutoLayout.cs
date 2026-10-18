using DevHub.Sdk.Core.Localization;

namespace DevHub.Diagrams.Core;

/// <summary>
/// Layered layout ("Arrange"): shapes in rows following the direction of the lines (an activity flows top to
/// bottom, subclasses sit under their parent when lines point up to it). Cycles (loops) are broken by ignoring the
/// lines that go back; within a row the order follows the neighbours (barycentre sweeps) so lines cross less.
/// </summary>
public static class AutoLayout
{
    /// <param name="ids">The shapes to arrange (others stay where they are; the arranged ones keep their top-left corner).</param>
    /// <param name="reversed">Edge types whose direction points "up" (generalization: child → parent), laid out upwards.</param>
    public static IDiagramOperation Layered(Diagram diagram, IReadOnlyCollection<string> ids, bool leftToRight = false,
        IReadOnlySet<string>? reversed = null, double gapAcross = 40, double gapAlong = 60)
    {
        var nodes = diagram.Nodes.Where(n => ids.Contains(n.Id)).ToList();
        if (nodes.Count < 2)
            return new Batch(L.T("Arrange"), []);
        var set = nodes.Select(n => n.Id).ToHashSet();
        var edges = diagram.Edges
            .Where(e => set.Contains(e.From) && set.Contains(e.To) && e.From != e.To)
            .Select(e => reversed?.Contains(e.Type) == true ? (From: e.To, To: e.From) : (e.From, e.To))
            .Distinct()
            .ToList();

        // Break cycles: depth-first from the shapes nothing leads to (a start node), dropping edges to a node on the
        // current path; the loop's way back is the one dropped.
        var order = nodes.OrderBy(n => n.Y).ThenBy(n => n.X).Select(n => n.Id).ToList();
        var sources = order.Where(id => edges.All(e => e.To != id)).ToList();
        var visitOrder = sources.Concat(order.Except(sources)).ToList();
        var acyclic = new List<(string From, string To)>();
        var state = new Dictionary<string, int>();   // 1 = on the path, 2 = done
        void Visit(string id)
        {
            state[id] = 1;
            foreach (var e in edges.Where(e => e.From == id))
            {
                if (state.GetValueOrDefault(e.To) == 1)
                    continue;
                acyclic.Add(e);
                if (state.GetValueOrDefault(e.To) == 0)
                    Visit(e.To);
            }
            state[id] = 2;
        }
        foreach (var id in visitOrder)
            if (state.GetValueOrDefault(id) == 0)
                Visit(id);

        // Layers: longest path from the sources.
        var layer = order.ToDictionary(id => id, _ => 0);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (from, to) in acyclic)
            {
                if (layer[to] < layer[from] + 1)
                {
                    layer[to] = layer[from] + 1;
                    changed = true;
                }
            }
        }

        var rows = layer.GroupBy(p => p.Value).OrderBy(g => g.Key)
            .Select(g => g.Select(p => p.Key).OrderBy(order.IndexOf).ToList()).ToList();

        // Order within rows by the average position of the neighbours in the row before (then after).
        for (var sweep = 0; sweep < 4; sweep++)
        {
            var down = sweep % 2 == 0;
            for (var r = down ? 1 : rows.Count - 2; down ? r < rows.Count : r >= 0; r += down ? 1 : -1)
            {
                var neighbourRow = rows[down ? r - 1 : r + 1];
                double Bary(string id)
                {
                    var ns = acyclic.Where(e => down ? e.To == id : e.From == id)
                        .Select(e => neighbourRow.IndexOf(down ? e.From : e.To)).Where(i => i >= 0).ToList();
                    return ns.Count == 0 ? rows[r].IndexOf(id) : ns.Average();
                }
                rows[r] = [.. rows[r].OrderBy(Bary)];
            }
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var origin = new DPoint(nodes.Min(n => n.X), nodes.Min(n => n.Y));
        var rowSizes = rows.Select(row => row.Max(id => leftToRight ? byId[id].Width : byId[id].Height)).ToList();
        var rowLengths = rows.Select(row => row.Sum(id => leftToRight ? byId[id].Height : byId[id].Width) + gapAcross * (row.Count - 1)).ToList();
        var widest = rowLengths.Max();

        var moves = new List<IDiagramOperation>();
        var along = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            var across = (widest - rowLengths[r]) / 2;   // rows centred on each other
            foreach (var id in rows[r])
            {
                var n = byId[id];
                var size = leftToRight ? n.Height : n.Width;
                var thickness = leftToRight ? n.Width : n.Height;
                var centring = (rowSizes[r] - thickness) / 2;
                var (x, y) = leftToRight
                    ? (origin.X + along + centring, origin.Y + across)
                    : (origin.X + across, origin.Y + along + centring);
                if (Math.Abs(x - n.X) > 0.01 || Math.Abs(y - n.Y) > 0.01)
                    moves.Add(new MoveNodes([id], x - n.X, y - n.Y));
                across += size + gapAcross;
            }
            along += rowSizes[r] + gapAlong;
        }
        // Hand-placed bends no longer fit.
        foreach (var e in diagram.Edges.Where(e => set.Contains(e.From) && set.Contains(e.To) && e.Waypoints.Count > 0))
            moves.Add(new SetEdgePath(e.Id, [], e.Routing, e.LabelAt));
        return new Batch(L.T("Arrange"), moves);
    }
}
