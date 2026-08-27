using System;
using System.Collections.Immutable;

namespace StardewModdingAPI.Framework.Health.Presentation;

/// <summary>An immutable source-backed row projection which materializes one bounded page at a time.</summary>
internal sealed class ModHealthVirtualRowSource<TSource, TRow>
{
    public const int MaxPageSize = 50;

    private readonly ImmutableArray<TSource> Source;
    private readonly ImmutableArray<int> SourceIndexes;
    private readonly bool UsesSourceIndexes;
    private readonly Func<TSource, TRow> Project;

    public int Count => this.UsesSourceIndexes ? this.SourceIndexes.Length : this.Source.Length;

    public ModHealthVirtualRowSource(ImmutableArray<TSource> source, Func<TSource, TRow> project)
        : this(source, default, false, project)
    {
    }

    private ModHealthVirtualRowSource(ImmutableArray<TSource> source, ImmutableArray<int> sourceIndexes, bool usesSourceIndexes, Func<TSource, TRow> project)
    {
        this.Source = source.IsDefault ? ImmutableArray<TSource>.Empty : source;
        this.SourceIndexes = sourceIndexes.IsDefault ? ImmutableArray<int>.Empty : sourceIndexes;
        this.UsesSourceIndexes = usesSourceIndexes;
        this.Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>Create a source which includes only matching rows without projecting them eagerly.</summary>
    public static ModHealthVirtualRowSource<TSource, TRow> Where(ImmutableArray<TSource> source, Predicate<TSource> predicate, Func<TSource, TRow> project)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        source = source.IsDefault ? ImmutableArray<TSource>.Empty : source;

        ImmutableArray<int>.Builder indexes = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < source.Length; i++)
        {
            if (predicate(source[i]))
                indexes.Add(i);
        }
        return new(source, indexes.ToImmutable(), true, project);
    }

    /// <summary>Project a clamped page of at most <see cref="MaxPageSize"/> rows.</summary>
    public ImmutableArray<TRow> GetPage(int offset, int count)
    {
        int start = Math.Clamp(offset, 0, this.Count);
        int requested = Math.Clamp(count, 0, MaxPageSize);
        int take = Math.Min(requested, this.Count - start);
        if (take == 0)
            return ImmutableArray<TRow>.Empty;

        ImmutableArray<TRow>.Builder rows = ImmutableArray.CreateBuilder<TRow>(take);
        for (int i = 0; i < take; i++)
        {
            int logicalIndex = start + i;
            int sourceIndex = this.UsesSourceIndexes ? this.SourceIndexes[logicalIndex] : logicalIndex;
            rows.Add(this.Project(this.Source[sourceIndex]));
        }
        return rows.MoveToImmutable();
    }
}
