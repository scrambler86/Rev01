// BOOKMARK: FILE = ITimeProvider.cs
using System;

namespace Game.Network
{
    /// <summary>Fornisce un timestamp (secondi) coerente con lo stack di rete.</summary>
    public interface ITimeProvider
    {
        double Now();
    }
}
