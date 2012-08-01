// Copyright (c) <2012> <Playdead>
// This file is subject to the MIT License as seen in the trunk of this repository
// Maintained by: <Kristian Kjems> <kristian.kjems+UnityVC@gmail.com>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using CommandLineExecution;

namespace VersionControl.Backend.SVN
{

    public class SVNCommands : MarshalByRefObject, IVersionControlCommands
    {
        private string workingDirectory = ".";
        private string userName;
        private string password;
        private string versionNumber;
        private readonly StatusDatabase statusDatabase = new StatusDatabase();
        private bool OperationActive { get { return currentExecutingOperation != null; } }
        private CommandLine currentExecutingOperation = null;
        private Thread refreshThread = null;
        private readonly object operationActiveLockToken = new object();
        private readonly object requestQueueLockToken = new object();
        private readonly object statusDatabaseLockToken = new object();
        private readonly List<string> localRequestQueue = new List<string>();
        private readonly List<string> remoteRequestQueue = new List<string>();
        private readonly List<string> remoteRequestRuleList = new List<string>();
        private volatile bool active = false;
        private volatile bool requestRefreshLoopStop = false;

        public SVNCommands()
        {
            StartRefreshLoop();
            AppDomain.CurrentDomain.DomainUnload += Unload;
            AppDomain.CurrentDomain.ProcessExit += Unload;
        }

        private void Unload(object sender, EventArgs args)
        {
            TerminateRefreshLoop();
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.DomainUnload -= Unload;
            AppDomain.CurrentDomain.ProcessExit -= Unload;
            TerminateRefreshLoop();
        }

        private void RefreshLoop()
        {
            try
            {
                while (!requestRefreshLoopStop)
                {
                    Thread.Sleep(200);
                    if (active) RefreshStatusDatabase();
                }
            }
            catch (ThreadAbortException) { }
            catch (AppDomainUnloadedException) { }
            catch (Exception e)
            {
                D.ThrowException(e);
            }
        }

        private void StartRefreshLoop()
        {
            if (refreshThread == null)
            {
                refreshThread = new Thread(RefreshLoop);
                refreshThread.Start();
            }
        }

        // This should only be used during termination of the host AppDomain or Process
        private void TerminateRefreshLoop()
        {
            active = false;
            requestRefreshLoopStop = true;
            if (currentExecutingOperation != null)
            {
                currentExecutingOperation.Dispose();
                currentExecutingOperation = null;
            }
            if (refreshThread != null)
            {
                refreshThread.Abort();
                refreshThread = null;
            }
        }

        public void Start()
        {
            active = true;
        }

        public void Stop()
        {
            active = false;
        }

        private void RefreshStatusDatabase()
        {
            List<string> localCopy = null;
            List<string> remoteCopy = null;

            lock (requestQueueLockToken)
            {
                if (localRequestQueue.Count > 0)
                {
                    localCopy = new List<string>(localRequestQueue.Except(remoteRequestQueue).Distinct());
                    localRequestQueue.Clear();
                }
                if (remoteRequestQueue.Count > 0)
                {
                    remoteCopy = new List<string>(remoteRequestQueue.Distinct());
                    remoteRequestQueue.Clear();
                }
            }
            //if (localCopy != null && localCopy.Count > 0) D.Log("Local Status : " + localCopy.Aggregate((a, b) => a + ", " + b));
            //if (remoteCopy != null && remoteCopy.Count > 0) D.Log("Remote Status : " + remoteCopy.Aggregate((a, b) => a + ", " + b));
            if (localCopy != null && localCopy.Count > 0) Status(localCopy, StatusLevel.Local);
            if (remoteCopy != null && remoteCopy.Count > 0) Status(remoteCopy, StatusLevel.Remote);
        }

        public bool IsReady()
        {
            return !OperationActive && active;
        }

        public void SetWorkingDirectory(string workingDirectory)
        {
            this.workingDirectory = workingDirectory;
        }

        public void SetUserCredentials(string userName, string password)
        {
            if (!string.IsNullOrEmpty(userName)) this.userName = userName;
            if (!string.IsNullOrEmpty(password)) this.password = password;
        }

        public VersionControlStatus GetAssetStatus(string assetPath)
        {
            lock (statusDatabaseLockToken)
            {
                assetPath = assetPath.Replace("\\", "/");
                return statusDatabase[assetPath];
            }
        }

        public IEnumerable<string> GetFilteredAssets(Func<string, VersionControlStatus, bool> filter)
        {
            lock (statusDatabaseLockToken)
            {
                return new List<string>(statusDatabase.Keys).Where(k => filter(k, statusDatabase[k])).ToList();
            }
        }

        public bool Status(StatusLevel statusLevel, DetailLevel detailLevel)
        {
            if (!active) return false;

            string arguments = "status --xml";
            if (statusLevel == StatusLevel.Remote) arguments += " -u";
            if (detailLevel == DetailLevel.Verbose) arguments += " -v";

            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteOperation(svnStatusTask, false);
            }

            if (commandLineOutput == null || commandLineOutput.Failed || string.IsNullOrEmpty(commandLineOutput.OutputStr) || !active) return false;
            try
            {
                var db = SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr);
                lock (statusDatabaseLockToken)
                {
                    foreach (var statusIt in db)
                    {
                        statusDatabase[statusIt.Key] = statusIt.Value;
                    }
                }
                OnStatusCompleted();
            }
            catch (XmlException)
            {
                return false;
            }
            return true;
        }

        public bool Status(IEnumerable<string> assets, StatusLevel statusLevel)
        {
            if (!active) return false;

            const int assetsPerStatus = 20;
            if (assets.Count() > assetsPerStatus)
            {
                return Status(assets.Take(assetsPerStatus), statusLevel) && Status(assets.Skip(assetsPerStatus), statusLevel);
            }

            string arguments = "status --xml -q -v ";
            if (statusLevel == StatusLevel.Remote) arguments += "-u ";
            else arguments += " --depth=empty ";
            arguments += ConcatAssetPaths(RemoveWorkingDirectoryFromPath(assets));
            lock (statusDatabaseLockToken)
            {
                foreach (var assetIt in assets)
                {
                    statusDatabase[assetIt] = new VersionControlStatus { assetPath = assetIt, reflectionLevel = VCReflectionLevel.Pending };
                }
            }
            CommandLineOutput commandLineOutput;
            using (var svnStatusTask = CreateSVNCommandLine(arguments))
            {
                commandLineOutput = ExecuteOperation(svnStatusTask, false);
            }
            if (commandLineOutput == null || commandLineOutput.Failed || string.IsNullOrEmpty(commandLineOutput.OutputStr) || !active) return false;
            try
            {
                var db = SVNStatusXMLParser.SVNParseStatusXML(commandLineOutput.OutputStr);
                lock (statusDatabaseLockToken)
                {
                    foreach (var statusIt in db)
                    {
                        statusDatabase[statusIt.Key] = statusIt.Value;
                    }
                }
                OnStatusCompleted();
            }
            catch (XmlException e)
            {
                D.ThrowException(e);
                return false;
            }
            return true;
        }

        private CommandLine CreateSVNCommandLine(string arguments)
        {
            arguments = "--non-interactive " + arguments;
            if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password))
            {
                arguments = " --username " + userName + " --password " + password + " --no-auth-cache " + arguments;
            }
            return new CommandLine("svn", arguments, workingDirectory);
        }

        private bool CreateOperation(string arguments)
        {
            if (!active) return false;

            CommandLineOutput commandLineOutput;
            using (var commandLineOperation = CreateSVNCommandLine(arguments))
            {
                commandLineOperation.OutputReceived += OnProgressInformation;
                commandLineOperation.ErrorReceived += OnProgressInformation;
                commandLineOutput = ExecuteOperation(commandLineOperation);
            }
            return !(commandLineOutput == null || commandLineOutput.Failed);
        }

        private CommandLineOutput ExecuteCommandLine(CommandLine commandLine)
        {
            CommandLineOutput commandLineOutput;
            try
            {
                D.Log(commandLine.ToString());
                currentExecutingOperation = commandLine;
                //System.Threading.Thread.Sleep(500); // emulate latency to SVN server
                commandLineOutput = commandLine.Execute();
            }
            catch (Exception e)
            {
                throw new VCCriticalException("Check that your commandline SVN client is installed corretly\n\n" + e.Message, commandLine.ToString(), e);
            }
            finally
            {
                currentExecutingOperation = null;
            }
            return commandLineOutput;
        }

        private CommandLineOutput ExecuteOperation(CommandLine commandLine, bool useOperationLock = true)
        {
            CommandLineOutput commandLineOutput;
            if (useOperationLock)
            {
                lock (operationActiveLockToken)
                {
                    commandLineOutput = ExecuteCommandLine(commandLine);
                }
            }
            else
            {
                commandLineOutput = ExecuteCommandLine(commandLine);
            }

            if (commandLineOutput.Arguments.Contains("ExceptionTest.txt"))
            {
                throw new VCException("Test Exception cast due to ExceptionTest.txt being a part of arguments", commandLine.ToString());
            }
            if (!string.IsNullOrEmpty(commandLineOutput.ErrorStr))
            {
                var errStr = commandLineOutput.ErrorStr;
                if (errStr.Contains("E730060") || errStr.Contains("Unable to connect") || errStr.Contains("is unreachable") || errStr.Contains("Operation timed out") || errStr.Contains("Can't connect to"))
                    throw new VCConnectionTimeoutException(errStr, commandLine.ToString());
                if (errStr.Contains("W160042") || errStr.Contains("Newer Version"))
                    throw new VCNewerVersionException(errStr, commandLine.ToString());
                if (errStr.Contains("W155007") || errStr.Contains("'" + workingDirectory + "'" + " is not a working copy"))
                    throw new VCCriticalException(errStr, commandLine.ToString());
                if (errStr.Contains("E160028") || errStr.Contains("is out of date"))
                    throw new VCOutOfDate(errStr, commandLine.ToString());
                if (errStr.Contains("E155037") || errStr.Contains("E155004") || errStr.Contains("run 'svn cleanup'"))
                    throw new VCLocalCopyLockedException(errStr, commandLine.ToString());
                if (errStr.Contains("W160035") || errStr.Contains("run 'svn cleanup'"))
                    throw new VCLockedByOther(errStr, commandLine.ToString());
                throw new VCException(errStr, commandLine.ToString());
            }
            return commandLineOutput;
        }

        private bool CreateAssetOperation(string arguments, IEnumerable<string> assets)
        {
            if (assets == null || !assets.Any()) return true;
            return CreateOperation(arguments + ConcatAssetPaths(assets)) && RequestStatus(assets);
        }

        private static string FixAtChar(string asset)
        {
            return asset.Contains("@") ? asset + "@" : asset;
        }

        private IEnumerable<string> RemoveWorkingDirectoryFromPath(IEnumerable<string> assets)
        {
            return assets.Select(a => a.Replace(workingDirectory, ""));
        }

        private static string ConcatAssetPaths(IEnumerable<string> assets)
        {
            assets = assets.Select(a => a.Replace("\\", "/"));
            assets = assets.Select(FixAtChar);
            if (assets.Any()) return " \"" + assets.Aggregate((i, j) => i + "\" \"" + j) + "\"";
            return "";
        }

        public virtual bool SetStatusRequestRule(IEnumerable<string> assets, StatusLevel statusLevel)
        {
            foreach (var assetIt in assets)
            {
                if (statusLevel == StatusLevel.Remote)
                {
                    if (!remoteRequestRuleList.Contains(assetIt))
                    {
                        remoteRequestRuleList.Add(assetIt);
                    }
                }
                else
                {
                    remoteRequestRuleList.Remove(assetIt);
                }
            }
            return true;
        }

        public virtual bool RequestStatus(IEnumerable<string> assets)
        {
            if (assets == null || assets.Count() == 0) return true;

            lock (requestQueueLockToken)
            {
                foreach (string assetIt in assets)
                {
                    if (remoteRequestRuleList.Contains(assetIt))
                    {
                        //D.Log(" Request Remote: " + asset + " : " + GetAssetStatus(asset).reflectionLevel);
                        remoteRequestQueue.Add(assetIt);
                    }
                    else
                    {
                        //D.Log(" Request Local : " + asset + " : " + GetAssetStatus(asset).reflectionLevel);
                        localRequestQueue.Add(assetIt);
                    }
                }
            }
            return true;
        }

        public bool Update(IEnumerable<string> assets = null)
        {
            if (assets == null || !assets.Any()) assets = new[] { workingDirectory };
            return CreateAssetOperation("update --force", assets);
        }

        public bool Commit(IEnumerable<string> assets, string commitMessage = "")
        {
            return CreateAssetOperation("commit -m \"" + commitMessage + "\"", assets);
        }

        public bool Add(IEnumerable<string> assets)
        {
            return CreateAssetOperation("add", assets);
        }

        public bool Revert(IEnumerable<string> assets)
        {
            return CreateAssetOperation("revert --depth=infinity", assets);
        }

        public bool Delete(IEnumerable<string> assets, OperationMode mode)
        {
            return CreateAssetOperation("delete" + (mode == OperationMode.Force ? " --force" : ""), assets);
        }

        public bool GetLock(IEnumerable<string> assets, OperationMode mode)
        {
            return CreateAssetOperation("lock" + (mode == OperationMode.Force ? " --force" : ""), assets);
        }

        public bool ReleaseLock(IEnumerable<string> assets)
        {
            return CreateAssetOperation("unlock", assets);
        }

        public bool ChangeListAdd(IEnumerable<string> assets, string changelist)
        {
            return CreateAssetOperation("changelist " + changelist, assets);
        }

        public bool ChangeListRemove(IEnumerable<string> assets)
        {
            return CreateAssetOperation("changelist --remove", assets);
        }

        public bool Checkout(string url, string path = "")
        {
            return CreateOperation("checkout \"" + url + "\" \"" + (path == "" ? workingDirectory : path) + "\"");
        }

        public bool Resolve(IEnumerable<string> assets, ConflictResolution conflictResolution)
        {
            if (conflictResolution == ConflictResolution.Ignore) return true;
            string conflictparameter = conflictResolution == ConflictResolution.Theirs ? "--accept theirs-full" : "--accept mine-full";
            return CreateAssetOperation("resolve " + conflictparameter, assets);
        }

        public bool Move(string from, string to)
        {
            return CreateOperation("move \"" + from + "\" \"" + to + "\"") && RequestStatus(new[] { from, to });
        }

        public string GetBasePath(string assetPath)
        {
            if (string.IsNullOrEmpty(versionNumber))
            {
                versionNumber = CreateSVNCommandLine("--version --quiet").Execute().OutputStr;
            }
            if (versionNumber.StartsWith("1.7"))
            {
                var svnInfo = CreateSVNCommandLine("info --xml " + assetPath).Execute();
                if (!svnInfo.Failed)
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(svnInfo.OutputStr);
                    var checksumNode = xmlDoc.GetElementsByTagName("checksum").Item(0);
                    var rootPathNode = xmlDoc.GetElementsByTagName("wcroot-abspath").Item(0);

                    if (checksumNode != null && rootPathNode != null)
                    {
                        string checksum = checksumNode.InnerText;
                        string firstTwo = checksum.Substring(0, 2);
                        string rootPath = rootPathNode.InnerText;
                        string basePath = rootPath + "/.svn/pristine/" + firstTwo + "/" + checksum + ".svn-base";
                        if (File.Exists(basePath)) return basePath;
                    }
                }
            }
            if (versionNumber.StartsWith("1.6"))
            {
                return Path.GetDirectoryName(assetPath) + "/.svn/text-base/" + Path.GetFileName(assetPath) + ".svn-base";
            }
            return "";
        }

        public bool CleanUp()
        {
            return CreateOperation("cleanup");
        }

        public void ClearDatabase()
        {
            lock (statusDatabaseLockToken)
            {
                statusDatabase.Clear();
            }
        }

        public void RemoveFromDatabase(IEnumerable<string> assets)
        {
            lock (statusDatabaseLockToken)
            {
                foreach (var assetIt in assets)
                {
                    statusDatabase.Remove(assetIt);
                }
            }
        }

        IEnumerable<string> RemoveFilesIfParentFolderInList(IEnumerable<string> assets)
        {
            var folders = assets.Where(a => Directory.Exists(a));
            assets = assets.Where(a => !folders.Any(f => a.StartsWith(f) && a != f));
            return assets;
        }

        public event Action<string> ProgressInformation;
        private void OnProgressInformation(string info)
        {
            if (ProgressInformation != null) ProgressInformation(info);
        }

        public event Action StatusCompleted;
        private void OnStatusCompleted()
        {
            if (StatusCompleted != null) StatusCompleted();
        }
    }
}
