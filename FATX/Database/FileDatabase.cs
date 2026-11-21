using FATX.FileSystem;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FATXTools.Database
{
    /// <summary>
    /// How cluster chains are rebuilt for deleted files. FATX zeroes the
    /// FAT chain on deletion so only the first cluster and the file size
    /// survive; the rest of the chain has to be predicted.
    /// </summary>
    public enum ChainStrategy
    {
        /// <summary>
        /// Assume the file occupied consecutive clusters starting at its
        /// first cluster (the original FATXTools behavior).
        /// </summary>
        Contiguous,

        /// <summary>
        /// Like <see cref="Contiguous"/>, but skip clusters that belong to
        /// active files which already existed when the deleted file was
        /// created. Those clusters were occupied at the time the deleted
        /// file was written, so the console's allocator had to skip them
        /// too. This recovers files that were written into fragmented free
        /// space.
        /// </summary>
        SkipOlderAllocated,
    }

    public class FileDatabase
    {
        /// <summary>
        /// Map of files according to its offset
        /// </summary>
        Dictionary<long, DatabaseFile> files;

        /// <summary>
        /// List of files at the root of the file system.
        /// </summary>
        List<DatabaseFile> root;

        /// <summary>
        /// Volume associated with this database.
        /// </summary>
        Volume volume;

        public FileDatabase(Volume volume)
        {
            this.files = new Dictionary<long, DatabaseFile>();
            this.root = new List<DatabaseFile>();
            this.volume = volume;

            MergeActiveFileSystem(volume);
        }

        /// <summary>
        /// Strategy used to rebuild the cluster chains of deleted files.
        /// </summary>
        public ChainStrategy ChainStrategy { get; set; } = ChainStrategy.SkipOlderAllocated;

        /// <summary>
        /// Returns the number of files in this database.
        /// </summary>
        /// <returns>Number of files in this database.</returns>
        public int Count()
        {
            return files.Count;
        }

        /// <summary>
        /// Update the file system in this database. This should be called after 
        /// any modifications are made to the database.
        /// </summary>
        public void Update()
        {
            // Now that every file is known, predict the cluster chains of the
            // deleted files.
            RebuildDeletedClusterChains();

            // Construct a new file system.
            root = new List<DatabaseFile>();

            // Link the file system together.
            LinkFileSystem();
        }

        public void Reset()
        {
            this.files = new Dictionary<long, DatabaseFile>();
            this.root = new List<DatabaseFile>();

            MergeActiveFileSystem(this.volume);
        }

        /// <summary>
        /// Build the file system.
        /// </summary>
        private void LinkFileSystem()
        {
            // Clear all previous links
            foreach (var file in files.Values)
            {
                file.Children = new List<DatabaseFile>();
                file.SetParent(null);
            }

            // Index the directories by the clusters that they occupy. This
            // replaces an O(directories * files * chain) search that was
            // painfully slow on large drives.
            var directoriesByCluster = new Dictionary<uint, List<DatabaseFile>>();
            foreach (var file in files.Values)
            {
                if (!file.IsDirectory() || file.ClusterChain == null)
                    continue;

                foreach (var cluster in file.ClusterChain)
                {
                    if (!directoriesByCluster.TryGetValue(cluster, out var list))
                    {
                        list = new List<DatabaseFile>(1);
                        directoriesByCluster[cluster] = list;
                    }
                    list.Add(file);
                }
            }

            // Link all of the files together. Visit files in a stable order
            // (by offset) so results are deterministic.
            foreach (var file in files.Values.OrderBy(f => f.Offset))
            {
                if (!directoriesByCluster.TryGetValue(file.Cluster, out var candidates))
                    continue;

                // Prefer active directories over deleted ones, they are the
                // authoritative owners of their clusters.
                DatabaseFile parent = null;
                foreach (var candidate in candidates.OrderBy(c => c.IsDeleted ? 1 : 0).ThenBy(c => c.Offset))
                {
                    if (candidate == file || IsAncestorOrSelf(file, candidate))
                    {
                        // Linking would create a loop (e.g. a deleted directory
                        // whose first cluster points at the cluster holding its
                        // own entry), which used to hang the tree view and the
                        // recovery code.
                        continue;
                    }

                    parent = candidate;
                    break;
                }

                if (parent != null)
                {
                    parent.Children.Add(file);
                    file.SetParent(parent);
                }
            }

            // Gather files at the root
            foreach (var file in files.Values.OrderBy(f => f.Offset))
            {
                if (!file.HasParent())
                {
                    root.Add(file);
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="ancestor"/> is <paramref name="node"/> or one of the ancestors of <paramref name="node"/>.
        /// </summary>
        private static bool IsAncestorOrSelf(DatabaseFile ancestor, DatabaseFile node)
        {
            int depth = 0;
            for (var p = node; p != null; p = p.GetParent())
            {
                if (p == ancestor)
                    return true;

                // Safety net, a file system cannot be this deep.
                if (++depth > 4096)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Merge the active file system into this database.
        /// </summary>
        /// <param name="volume">The active file system.</param>
        private void MergeActiveFileSystem(Volume volume)
        {
            RegisterDirectoryEntries(volume.GetRoot());
            RebuildDeletedClusterChains();
        }

        /// <summary>
        /// Register directory entries in bulk.
        /// </summary>
        /// <param name="dirents"></param>
        private void RegisterDirectoryEntries(List<DirectoryEntry> dirents)
        {
            foreach (var dirent in dirents)
            {
                if (dirent.IsDeleted())
                {
                    AddFile(dirent, true);
                }
                else
                {
                    AddFile(dirent, false);

                    if (dirent.IsDirectory())
                    {
                        RegisterDirectoryEntries(dirent.Children);
                    }
                }
            }
        }

        /// <summary>
        /// (Re)generates cluster chains for every deleted file whose chain was
        /// not explicitly set by the user or loaded from a database.
        /// </summary>
        public void RebuildDeletedClusterChains()
        {
            var predictor = new ChainPredictor(volume);
            if (ChainStrategy == ChainStrategy.SkipOlderAllocated)
            {
                foreach (var f in files.Values)
                {
                    if (!f.IsDeleted)
                        predictor.AddActiveChain(f.ClusterChain, f.CreationTime.AsDateTime(), f);
                }
                predictor.Seal();
            }

            foreach (var file in files.Values)
            {
                if (!file.IsDeleted || file.HasCustomClusterChain)
                    continue;

                var d = file.GetDirent();
                file.ClusterChain = predictor.Predict(d.FirstCluster, d.FileSize, d.IsDirectory(),
                    d.CreationTime.AsDateTime(), ChainStrategy);
            }
        }

        /// <summary>
        /// Get a file by the offset into the file system.
        /// </summary>
        /// <param name="offset">File area offset</param>
        /// <returns>DatabaseFile from this database</returns>
        public DatabaseFile GetFile(long offset)
        {
            if (files.ContainsKey(offset))
            {
                return files[offset];
            }

            return null;
        }

        /// <summary>
        /// Get a file by an instance of a DirectoryEntry.
        /// </summary>
        /// <param name="dirent">DirectoryEntry instance</param>
        /// <returns>DatabaseFile from this database</returns>
        public DatabaseFile GetFile(DirectoryEntry dirent)
        {
            if (files.ContainsKey(dirent.Offset))
            {
                return files[dirent.Offset];
            }

            return null;
        }

        /// <summary>
        /// Create and initialize a new DatabaseFile for this DirectoryEntry.
        /// </summary>
        private DatabaseFile CreateDatabaseFile(DirectoryEntry dirent, bool deleted)
        {
            var databaseFile = new DatabaseFile(dirent, deleted);
            files[dirent.Offset] = databaseFile;

            if (deleted)
            {
                // A contiguous chain for now; Update() refines it once all
                // files are known.
                databaseFile.ClusterChain = new ChainPredictor(volume).Predict(dirent.FirstCluster, dirent.FileSize,
                    dirent.IsDirectory(), dirent.CreationTime.AsDateTime(), ChainStrategy.Contiguous);
            }
            else
            {
                databaseFile.ClusterChain = this.volume.GetClusterChain(dirent);
            }

            return databaseFile;
        }

        /// <summary>
        /// Add and initialize a new DatabaseFile.
        /// </summary>
        /// <param name="dirent">The associated DirectoryEntry</param>
        /// <param name="deleted">Whether or not this file was deleted</param>
        public DatabaseFile AddFile(DirectoryEntry dirent, bool deleted)
        {
            // Create the file if it was not already added
            if (!files.TryGetValue(dirent.Offset, out var existing))
            {
                return CreateDatabaseFile(dirent, deleted);
            }

            return existing;
        }

        /// <summary>
        /// Get all files from this database.
        /// </summary>
        /// <returns></returns>
        public Dictionary<long, DatabaseFile> GetFiles()
        {
            return files;
        }

        /// <summary>
        /// Get the root files from this database's file system.
        /// </summary>
        /// <returns></returns>
        public List<DatabaseFile> GetRootFiles()
        {
            return root;
        }

        /// <summary>
        /// Get the volume associated with this database.
        /// </summary>
        /// <returns></returns>
        public Volume GetVolume()
        {
            return volume;
        }
    }
}
