namespace GitCommands
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using GitCommands.Git;

    public interface IGdataNameResolver
    {
        string ResolveName(GitItem item);
        string ResolveName(GitRevision revision, GitItemStatus item);
        Task<string> ResolveNameAsync(GitItem item, CancellationToken cancellationToken);
        Task<string> ResolveNameAsync(GitRevision revision, GitItemStatus item, CancellationToken cancellationToken);
        Task<string> ResolveNameAsync(GitRevision firstRevision, GitRevision secondRevision, GitItemStatus item, CancellationToken cancellationToken);
    }

    public class FileCacheEntry
    {
        private string _guid;
        public string Guid
        {
            get => _guid;
        }

        private string _fileName;
        public string FileName
        {
            get => _fileName;
        }

        private string _contents;
        public string Contents
        {
            get => _contents;
        }

        private string _gdataName;
        public string GdataName
        {
            get => _gdataName;
        }

        public FileCacheEntry(string guid, string fileName, string contents, string gdataName)
        {
            _guid = guid;
            _fileName = fileName;
            _contents = contents;
            _gdataName = gdataName;
        }
    }

    public class GdataNameResolver : IGdataNameResolver
    {
        private static IDictionary<string, IList<FileCacheEntry>> DataCache = new Dictionary<string, IList<FileCacheEntry>>();

        private readonly Func<GitModule> _getModule;

        private Mutex _mutex = new Mutex();

        public GdataNameResolver(Func<GitModule> getModule)
        {
            _getModule = getModule;
        }

        private string GetGdataNameFromCache(string workDirPath, string guid, string fileName)
        {
            if (!DataCache.ContainsKey(workDirPath))
            {
                return null;
            }

            foreach (var item in DataCache[workDirPath])
            {
                if (item.Guid == guid && item.FileName == fileName)
                {
                    return item.GdataName;
                }
            }

            return null;
        }

        private void AddToCache(string workDirPath, string guid, string fileName, string contents, string gdataName)
        {
            // no error check, cache data check here!
            _mutex.WaitOne();

            if (!DataCache.ContainsKey(workDirPath))
            {
                DataCache[workDirPath] = new List<FileCacheEntry>
                {
                    new FileCacheEntry(guid, fileName, contents, gdataName)
                };
            }
            else
            {
                DataCache[workDirPath].Add(new FileCacheEntry(guid, fileName, contents, gdataName));
            }

            _mutex.ReleaseMutex();
        }

        private bool IsGdataFileHasName(string fileName)
        {
            if (fileName.EndsWith(".ebguide"))
            {
                return false;
            }

            var exclude = new List<string>()
            {
                "datapool.gdata",
                "events.gdata",
                "eventgroups.gdata",
                "languages.gdata",
                "skins.gdata",
                "contexts.gdata"
            };

            foreach (var name in exclude)
            {
                if (fileName.EndsWith(name))
                {
                    return false;
                }
            }

            // Vta
            if (!fileName.EndsWith(".gdata"))
            {
                return false;
            }

            return true;
        }

        public string ResolveName(GitItem item)
        {
            if (item == null || item.ObjectId == null)
            {
                return null;
            }

            if (!IsGdataFileHasName(item.FileName))
            {
                return null;
            }

            var gitModule = _getModule();
            var gdataName = GetGdataNameFromCache(gitModule.WorkingDir, item.Guid, item.FileName);
            if (gdataName != null)
            {
                return gdataName;
            }

            var contents = gitModule.GetFileText(item.ObjectId, System.Text.Encoding.UTF8);
            gdataName = GetGdataName(contents);

            if (gdataName != null)
            {
                AddToCache(gitModule.WorkingDir, item.Guid, item.FileName, null, gdataName);
            }

            return gdataName;
        }

        private string GetGdataName(string text)
        {
            text = text.Replace("\r", "");
            var lines = text.SplitLines();

            GetGdataVersion(lines, out int major, out int minor, out int subminor, out string suffix);

            if (major == 6 && minor >= 5 && minor < 8)
            {
                return GuessGdataObjectName(lines, false);
            }
            else if (major == 6 && minor >= 8)
            {
                return GuessGdataObjectName(lines, true);
            }

            return null;
        }

        private static string GuessGdataObjectName(string[] lines, bool is68 = true)
        {
            Regex firstLineHasNameProp = new Regex(@"^\tname: ""(.+)""$");

            if (!is68)
            {
                firstLineHasNameProp = new Regex(@"^\t#\d+? name :: string : ""(.+)"";$");
            }

            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];

                var match = firstLineHasNameProp.Match(line);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }

                i = i + 1;
            }

            return null;
        }

        private void GetGdataVersion(string[] lines, out int major, out int minor, out int subminor, out string suffix)
        {
            major = 0;
            minor = 0;
            subminor = 0;
            suffix = null;
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                Regex versionInfo = new Regex(@"^EBGUIDE (\d+?)\.(\d+?)\.(\d+?)\.(.+);$");
                var matches = versionInfo.Matches(line);

                if (matches.Count > 0)
                {
                    foreach (Match match in matches)
                    {
                        major = int.Parse(match.Groups[1].Value);
                        minor = int.Parse(match.Groups[2].Value);
                        subminor = int.Parse(match.Groups[3].Value);
                        suffix = match.Groups[4].Value;
                        return;
                    }
                }

                i = i + 1;
            }

            return;
        }

        public string ResolveName(GitRevision revision, GitItemStatus item)
        {
            if (item == null)
            {
                return null;
            }

            var gitModule = _getModule();

            if (!IsGdataFileHasName(item.Name))
            {
                return null;
            }

            try
            {
                if (revision.ObjectId.IsArtificial)
                {
                    // try to find from local
                    var path = Path.Combine(gitModule.WorkingDir, item.Name);
                    if (File.Exists(path))
                    {
                        string text = File.ReadAllText(path);
                        return GetGdataName(text);
                    }
                }

                var x = gitModule.GetTreeFiles(revision.ObjectId, full: true);
                foreach (var y in x)
                {
                    if (item.Name == y.Name)
                    {
                        var gdataName = GetGdataNameFromCache(gitModule.WorkingDir, y.TreeGuid.ToString(), y.Name);

                        if (gdataName == null)
                        {
                            var text = gitModule.GetFileText(y.TreeGuid, Encoding.UTF8);
                            gdataName = GetGdataName(text);

                            if (gdataName != null)
                            {
                                AddToCache(gitModule.WorkingDir, y.TreeGuid.ToString(), y.Name, null, gdataName);
                                return gdataName;
                            }
                        }
                        else
                        {
                            return gdataName;
                        }
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        public async Task<string> ResolveNameAsync(GitItem item, CancellationToken cancellationToken)
        {
            if (item == null)
            {
                return null;
            }

            var gitModule = _getModule();

            var name = await Task.Run<string>(
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    return ResolveName(item);
                }, cancellationToken);
            return name;
        }

        public async Task<string> ResolveNameAsync(GitRevision revision, GitItemStatus item, CancellationToken cancellationToken)
        {
            if (item == null || revision == null)
            {
                return null;
            }

            var gitModule = _getModule();

            var name = await Task.Run<string>(
                () =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return null;
                    }

                    return ResolveName(revision, item);
                }, cancellationToken);

            return name;
        }

        public async Task<string> ResolveNameAsync(GitRevision firstRevision, GitRevision secondRevision, GitItemStatus item, CancellationToken cancellationToken)
        {
            if (item == null)
            {
                return null;
            }

            var name = await ResolveNameAsync(firstRevision, item, cancellationToken);
            if (name != null)
            {
                return name;
            }

            name = await ResolveNameAsync(secondRevision, item, cancellationToken);
            return name;
        }
    }
}
