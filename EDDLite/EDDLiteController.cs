﻿/*
 * Copyright © 2020-2022 EDDiscovery development team
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not use this
 * file except in compliance with the License. You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software distributed under
 * the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF
 * ANY KIND, either express or implied. See the License for the specific language
 * governing permissions and limitations under the License.
 */

using EliteDangerousCore;
using System;
using System.Collections.Generic;
using System.Threading;

namespace EDDLite
{
    public class EDDLiteController
    {
        public bool RequestRescan { get; set; } = false;
        public Action<HistoryEntry> RefreshFinished { get; set; } = null;           // on UI thread
        public Action<HistoryEntry,bool,bool> NewEntry { get; set; } = null;        // on UI thread
        public Action<UIEvent> NewUI { get; set; } = null;                          // on UI thread
        public Action<string> ProgressEvent { get; set; } = null;                   // on UI thread
        public Action<string> LogLine { get; set; } = null;                         // on UI thread
        public Action Closed { get; set; } = null;                                  // on UI thread

        const int uirecentlimit = 50;         // entries marked as close to end as this are marked recent and shown to user
        const int journalstoreload = 4;   // how many journals back to read and replay to get info on

        public void Start(Action<Action> invokeOnUiThread)
        {
            InvokeOnUiThread = invokeOnUiThread;
            journalqueuedelaytimer = new Timer(DelayPlay, null, Timeout.Infinite, Timeout.Infinite);
            controllerthread = new Thread(ControllerThread);
            controllerthread.Start();
        }

        CancellationTokenSource PendingClose = new CancellationTokenSource();                // cancel requested

        // Request it. Controller will call Closed() when it complies
        public void RequestStop()
        {
            System.Diagnostics.Debug.WriteLine($"{BaseUtils.AppTicks.TickCountLap("CLS",true)} Request controller close");
            PendingClose.Cancel();
        }

        private void ControllerThread()
        {
            journalmonitor = new EDJournalUIScanner(InvokeOnUiThread);
            journalmonitor.OnNewFilteredJournalEntry += (je, sr) => { Entry(je, false, true); };
            journalmonitor.OnNewUIEvent += (ui, sr) => { InvokeOnUiThread(() => NewUI?.Invoke(ui)); };

            StartWatchersAndReplayLastStoredEntries();

            while (!PendingClose.IsCancellationRequested)
            {
                if ( RequestRescan )
                {
                    RequestRescan = false;

                    InvokeOnUiThread(() => LogLine?.Invoke("Re-reading Journals") );
                    journalmonitor.StopMonitor();

                    StartWatchersAndReplayLastStoredEntries();
                }

                Thread.Sleep(100);
            }

            journalmonitor.StopMonitor();

            journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);
            journalqueuedelaytimer.Dispose();

            InvokeOnUiThread(() => { Closed.Invoke(); });
        }

        private void StartWatchersAndReplayLastStoredEntries()
        {
            System.Diagnostics.Debug.WriteLine($"{BaseUtils.AppTicks.TickCountLap("MT")} Controller start watcher started");
            ResetStats();

            string stdfolder = EDDOptions.Instance.DefaultJournalFolder.HasChars() ? EDDOptions.Instance.DefaultJournalFolder : FrontierFolder.FolderName();     // may be null

            string[] stdfolders = stdfolder != null ? new string[] { stdfolder} : new string[0];

            journalmonitor.SetupWatchers(stdfolders, "Journal*.log", DateTime.MinValue);

            InvokeOnUiThread(() => LogLine?.Invoke($"Reading back {journalstoreload} Journals"));

            List<JournalEntry> toprocess = new List<JournalEntry>();        // we accumulate in a list of the journal entries from the previous N journals
            journalmonitor.ParseJournalFilesOnWatchers(UpdateWatcher, PendingClose.Token, DateTime.MinValue, journalstoreload, toprocess);

            System.Diagnostics.Debug.WriteLine($"Play thru {toprocess.Count} to get state");

            InvokeOnUiThread(() =>      // synchronous, in UI thread.. this means controller won't stop until this is completely done
            {
                for (int i = 0; i < toprocess.Count && !PendingClose.IsCancellationRequested; i++)           // fill up Entries with events (if Stop it flag occurs we give up)
                {
                    //System.Diagnostics.Debug.WriteLine($"{BaseUtils.AppTicks.TickCount} Send {i}");

                    Entry(toprocess[i], true, i >= toprocess.Count - uirecentlimit);
                    if ( i % 100 == 0 )
                        System.Windows.Forms.Application.DoEvents();        // just keep the message loop happy as we bombard the UI thread with stuff to do
                };

                LogLine?.Invoke($"Finished reading Journals");

                //InvokeAsyncOnUiThread(() => { RefreshFinished?.Invoke(currenthe); });
                RefreshFinished?.Invoke(hlastprocessed);
            });

            journalmonitor.StartMonitor(false);
            System.Diagnostics.Debug.WriteLine($"{BaseUtils.AppTicks.TickCountLap("MT")} Controller start watcher finished");
        }

        private void UpdateWatcher(int p, string s) // in thread
        {
            InvokeOnUiThread(() => { ProgressEvent?.Invoke(s); });
            System.Diagnostics.Debug.WriteLine("Watcher Update " + p + " " + s);
        }

        DateTime lastutc;

        private void ResetStats(bool full = true)
        {
            hlastprocessed = null;
            outfitting = new OutfittingList();
            shipinformationlist = new ShipList();
            matlist = new MaterialCommoditiesMicroResourceList();
            missionlistaccumulator = new MissionListAccumulator(); // and mission list..
            cashledger = new Ledger();

            if (full)
            {
                lastutc = DateTime.Now.AddYears(-100);
                currentcmdrnr = -1;
            }
        }

        private Queue<HistoryEntry> journalqueue = new Queue<HistoryEntry>();
        private System.Threading.Timer journalqueuedelaytimer;

        // on UI thread. hooked into journal monitor and receives new entries.. Also call if you programatically add an entry
        public void Entry(JournalEntry je, bool stored, bool recent)        
        {
            System.Diagnostics.Debug.Assert(System.Windows.Forms.Application.MessageLoop);

            if (je.EventTimeUTC >= lastutc)     // in case we get them fed in the wrong order, or during stored reply we have two playing, only take the latest one
            {
               // System.Diagnostics.Debug.WriteLine("Controller Entry " + stored + ":" + recent + ":" + EDCommander.GetCommander(je.CommanderId).Name + ":" + je.EventTypeStr);

                if (je.CommanderId != currentcmdrnr)
                {
                    ResetStats(false);
                    currentcmdrnr = je.CommanderId;
                    EDCommander.CurrentCmdrID = currentcmdrnr;
                }

                // see HistoryList::MakeHistoryEntry for a EDD analogue

                HistoryEntry he = HistoryEntry.FromJournalEntry(je, hlastprocessed, null);          // we don't accumulate system names->star classes so its nul

                he.UnfilteredIndex = (hlastprocessed?.UnfilteredIndex ?? -1) + 1;
                he.UpdateMaterialsCommodities( matlist.Process(je, hlastprocessed?.journalEntry, he.Status.TravelState == HistoryEntryStatus.TravelStateType.SRV));

                // dont do suit, weapon, loadouts

                cashledger.Process(je);
                he.Credits = cashledger.CashTotal;
                he.Loan = cashledger.Loan;
                he.Assets = cashledger.Assets;

                // no shipyard
                outfitting.Process(je);

                // no carrier

                Tuple<Ship, ShipModulesInStore> ret = shipinformationlist.Process(je, he.WhereAmI, he.System, he.Status.IsInMultiPlayer);
                he.UpdateShipInformation(ret.Item1);
                he.UpdateShipStoredModules(ret.Item2);

                he.UpdateMissionList(missionlistaccumulator.Process(je, he.System, he.WhereAmI));

                // don't do update engineering

                he.UpdateTravelStatus(hlastprocessed);

                hlastprocessed = he;
                lastutc = je.EventTimeUTC;

                int playdelay = (je.EventTypeID == JournalTypeEnum.FSSSignalDiscovered) ? 2000 : 0;     // have we got a delayable entry

                // if not stored entry, and merge says delaying to see if a companion event occurs. add it to list. Set timer so we pick it up

                if (!stored && playdelay > 0)  
                {
                    System.Diagnostics.Debug.WriteLine(Environment.TickCount + " Delay Play queue " + je.EventTypeID + " Delay for " + playdelay);
                    journalqueue.Enqueue(he);
                    journalqueuedelaytimer.Change(playdelay, Timeout.Infinite);
                }
                else
                {
                    journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);  // stop the timer, but if it occurs before this, not the end of the world
                    journalqueue.Enqueue(he);  // add it to the play list.
                    PlayJournalList(stored,recent);    // and play
                }
            }
            else
            {
                //System.Diagnostics.Debug.WriteLine("Rejected older JE " + stored + ":" + recent + ":" + EDCommander.GetCommander(je.CommanderId).Name + " " + je.EventTypeStr);
            }
        }

        public void DelayPlay(Object s)             // timer thread timeout after play delay.. 
        {
            System.Diagnostics.Debug.WriteLine(Environment.TickCount + " Delay Play timer executed");
            journalqueuedelaytimer.Change(Timeout.Infinite, Timeout.Infinite);
            InvokeOnUiThread(() =>
            {
                PlayJournalList(false,true);
            });
        }

        void PlayJournalList(bool stored, bool recent)
        {
            System.Diagnostics.Debug.Assert(System.Windows.Forms.Application.MessageLoop);

            while (journalqueue.Count > 0)      // dequeue
            {
                var current = journalqueue.Dequeue();
                //System.Diagnostics.Trace.WriteLine($"PlayJournalList {current.EventTimeUTC} {current.EntryType}");

                if (!stored)
                {
                    while (journalqueue.Count > 0)     // merge back 
                    {
                        var peek = journalqueue.Peek(); // is there a next one?

                        if (peek != null && current.EntryType == peek.EntryType)        // if same event again
                        {
                            if (current.EntryType == JournalTypeEnum.FSSSignalDiscovered) // if mergeable
                            {
                                var jdprev = current.journalEntry as EliteDangerousCore.JournalEvents.JournalFSSSignalDiscovered;
                                var jd = peek.journalEntry as EliteDangerousCore.JournalEvents.JournalFSSSignalDiscovered;

                                if (jdprev.Signals[0].SystemAddress == jd.Signals[0].SystemAddress)     // only if same system address
                                {
                                    jdprev.Add(jd);     // merge! and waste
                                    System.Diagnostics.Trace.WriteLine($"PlayJournalList Merge {current.EntryType}");
                                    journalqueue.Dequeue();                     // remove it
                                }
                            }
                            else
                                break;
                        }
                        else
                            break;
                    }
                }

                NewEntry?.Invoke(current, stored, recent);      // dispatch possibly merged candidate
            }
        }


        public List<MaterialCommodityMicroResource> GetMatList(HistoryEntry he)
        {
            return matlist.Get(he.MaterialCommodity);
        }

        public Dictionary<string,MaterialCommodityMicroResource> GetMatDict(HistoryEntry he)
        {
            return matlist.GetDict(he.MaterialCommodity);
        }

        public List<MissionState> GetCurrentMissionList(HistoryEntry he)
        {
            return missionlistaccumulator.GetAllCurrentMissions(he.MissionList,he.EventTimeUTC);
        }

        private HistoryEntry hlastprocessed;
        private OutfittingList outfitting;
        private ShipList shipinformationlist;
        private MaterialCommoditiesMicroResourceList matlist;
        private MissionListAccumulator missionlistaccumulator;
        private Ledger cashledger;
        private int currentcmdrnr;
        private Thread controllerthread;
        private EDJournalUIScanner journalmonitor;
        private Action<Action> InvokeOnUiThread;


    }
}