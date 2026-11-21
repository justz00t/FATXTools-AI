using FATX.FileSystem;
using FATXTools.Database;
using System.Collections.Generic;

namespace FATX.Analyzers
{
    public class IntegrityAnalyzer
    {
        /// <summary>
        /// Active volume to analyze against.
        /// </summary>
        private Volume volume;

        private FileDatabase database;

        /// <summary>
        /// Mapping of cluster indexes to a list of entities that occupy it.
        /// </summary>
        private Dictionary<uint, List<DatabaseFile>> clusterMap;

        public IntegrityAnalyzer(Volume volume, FileDatabase database)
        {
            this.volume = volume;
            this.database = database;

            // Now that we have registered them, let's update the cluster map
            UpdateClusterMap();
        }

        private void UpdateClusters(DatabaseFile databaseFile)
        {
            if (databaseFile.ClusterChain == null)
                return;

            foreach (var cluster in databaseFile.ClusterChain)
            {
                // Ignore clusters outside of the volume instead of crashing.
                if (cluster == 0 || cluster >= volume.MaxClusters)
                    continue;

                if (!clusterMap.TryGetValue(cluster, out var occupants))
                {
                    occupants = new List<DatabaseFile>(1);
                    clusterMap[cluster] = occupants;
                }

                if (!occupants.Contains(databaseFile))
                    occupants.Add(databaseFile);
            }
        }

        private void UpdateClusterMap()
        {
            // Sparse map: only clusters that are actually claimed by a file
            // get an entry. Pre-allocating one list per cluster used several
            // gigabytes of memory on large Xbox 360 drives.
            clusterMap = new Dictionary<uint, List<DatabaseFile>>();

            foreach (var pair in database.GetFiles())
            {
                var databaseFile = pair.Value;

                // We handle active cluster chains conventionally
                if (!databaseFile.IsDeleted)
                {
                    UpdateClusters(databaseFile);
                }
                // Otherwise, we generate an artificial cluster chain
                else
                {
                    // TODO: Add a blocklist setting
                    if (databaseFile.FileName.StartsWith("xdk_data") ||
                        databaseFile.FileName.StartsWith("xdk_file") ||
                        databaseFile.FileName.StartsWith("tempcda"))
                    {
                        // These are usually always large and/or corrupted
                        // TODO: still don't really know what these files are
                        continue;
                    }

                    UpdateClusters(databaseFile);
                }
            }
        }

        public void Update()
        {
            UpdateClusterMap(); // Update clusterMap
            UpdateCollisions(); // Update collisions (Do the collision check)
            PerformRanking();   // Rank all clusters
        }

        private void UpdateCollisions()
        {
            foreach (var databaseFile in database.GetFiles().Values)
            {
                databaseFile.SetCollisions(FindCollidingClusters(databaseFile));
            }
        }

        private List<uint> FindCollidingClusters(DatabaseFile databaseFile)
        {
            // Get a list of cluster who are possibly corrupted
            List<uint> collidingClusters = new List<uint>();

            // for each cluster used by this dirent, check if other dirents are
            // also claiming it.
            if (databaseFile.ClusterChain == null)
                return collidingClusters;

            foreach (var cluster in databaseFile.ClusterChain)
            {
                if (clusterMap.TryGetValue(cluster, out var occupants) && occupants.Count > 1)
                {
                    collidingClusters.Add(cluster);
                }
            }

            return collidingClusters;
        }

        private bool WasModifiedLast(DatabaseFile databaseFile, List<uint> collisions)
        {
            var dirent = databaseFile.GetDirent();
            foreach (var cluster in collisions)
            {
                if (!clusterMap.TryGetValue(cluster, out var clusterEnts))
                    continue;
                foreach (var ent in clusterEnts)
                {
                    var entDirent = ent.GetDirent();

                    // Skip when we encounter the same dirent
                    if (dirent.Offset == entDirent.Offset)
                    {
                        continue;
                    }

                    // An active file sharing the cluster always wins: its data
                    // is what is physically there now.
                    if (!ent.IsDeleted)
                    {
                        return false;
                    }

                    // Use the last write time (when data was written). The
                    // last access time is updated on reads and says nothing
                    // about who owns the data.
                    if (dirent.LastWriteTime.AsDateTime() < entDirent.LastWriteTime.AsDateTime())
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void DoRanking(DatabaseFile databaseFile)
        {
            var dirent = databaseFile.GetDirent();

            // Rank 1
            // - Part of active file system
            // - Not deleted
            // Rank 2
            // - Recovered
            // - No conflicting clusters detected
            // Rank 3
            // - Recovered
            // - Some conflicting clusters
            // - Most recent data written
            // Rank 4
            // - Recovered
            // - Some conflicting clusters
            // - Not most recent data written
            // Rank 5
            // - Recovered
            // - All clusters overwritten

            if (!databaseFile.IsDeleted)
            {
                databaseFile.SetRanking((0));
            }
            else if (databaseFile.ClusterChain == null || (databaseFile.ClusterChain.Count == 0 && databaseFile.FileSize > 0))
            {
                // Location of the data is unknown (first cluster was zeroed).
                databaseFile.SetRanking((4));
            }
            else
            {
                // File was deleted
                var collisions = databaseFile.GetCollisions();
                if (collisions.Count == 0)
                {
                    databaseFile.SetRanking((1));
                }
                else
                {
                    // File has colliding clusters
                    if (WasModifiedLast(databaseFile, collisions))
                    {
                        // This file appears to have been written most recently.
                        databaseFile.SetRanking((2));
                    }
                    else
                    {
                        // File was predicted to be overwritten
                        var numClusters = databaseFile.ClusterChain.Count;
                        if (collisions.Count < numClusters)
                        {
                            // Not every cluster was overwritten
                            databaseFile.SetRanking((3));
                        }
                        else
                        {
                            // Every cluster appears to have been overwritten
                            databaseFile.SetRanking((4));
                        }
                    }
                }
            }
        }

        private void PerformRanking()
        {
            foreach (var pair in database.GetFiles())
            {
                DoRanking(pair.Value);
            }
        }

        private static readonly List<DatabaseFile> NoOccupants = new List<DatabaseFile>();

        public List<DatabaseFile> GetClusterOccupants(uint cluster)
        {
            if (clusterMap.TryGetValue(cluster, out var occupants))
            {
                return occupants;
            }

            // Keep the old contract: null for clusters outside of the volume,
            // an empty list for unused clusters.
            return cluster < volume.MaxClusters ? NoOccupants : null;
        }
    }
}
