/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Connectors.Friends;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// W2W social networking
    /// </summary>
    public class HGFriendsService : IHGFriendsService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        static bool m_Initialized = false;

        protected static IGridUserService m_GridUserService;
        protected static IGridService m_GridService;
        protected static IGatekeeperService m_GatekeeperService;
        protected static IFriendsService m_FriendsService;
        protected static IPresenceService m_PresenceService;
        protected static IUserAccountService m_UserAccountService;
        protected static IUserAgentService m_UserAgentService; // optional; enables notifications for traveling friends
        protected static IFriendsSimConnector m_FriendsLocalSimConnector; // standalone, points to HGFriendsModule
        protected static FriendsSimConnector m_FriendsSimConnector; // grid

        private static string m_ConfigName = "HGFriendsService";

        public HGFriendsService(IConfigSource config, String configName, IFriendsSimConnector localSimConn)
        {
            if (m_FriendsLocalSimConnector == null)
                m_FriendsLocalSimConnector = localSimConn;

            if (!m_Initialized)
            {
                m_Initialized = true;

                if (configName != String.Empty)
                    m_ConfigName = configName;

                Object[] args = [ config ];

                IConfig serverConfig = config.Configs[m_ConfigName];
                if (serverConfig == null)
                    throw new Exception(String.Format("No section {0} in config file", m_ConfigName));

                string theService = serverConfig.GetString("FriendsService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No FriendsService in config file " + m_ConfigName);
                m_FriendsService = ServerUtils.LoadPlugin<IFriendsService>(theService, args);

                theService = serverConfig.GetString("UserAccountService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No UserAccountService in " + m_ConfigName);
                m_UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(theService, args);

                theService = serverConfig.GetString("GridService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No GridService in " + m_ConfigName);
                m_GridService = ServerUtils.LoadPlugin<IGridService>(theService, args);

                theService = serverConfig.GetString("PresenceService", string.Empty);
                if (theService.Length == 0)
                    throw new Exception("No PresenceService in " + m_ConfigName);
                m_PresenceService = ServerUtils.LoadPlugin<IPresenceService>(theService, args);

                m_FriendsSimConnector = new FriendsSimConnector();

                // Lets a just-logged-out user's offline notice to traveling friends
                // pass when the logout was handled by another Robust process (split
                // setups), via the shared GridUser logout time. Optional: without it
                // only the in-process recent-logout window applies.
                theService = serverConfig.GetString("GridUserService",
                    "OpenSim.Services.UserAccountService.dll:GridUserService");
                try
                {
                    m_GridUserService = ServerUtils.LoadPlugin<IGridUserService>(theService, args);
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: GridUserService '{0}' failed to load: {1}", theService, e.Message);
                }
                if (m_GridUserService == null)
                    m_log.WarnFormat("[HGFRIENDS SERVICE]: Could not load GridUserService '{0}'; offline notices to traveling friends rely on the in-process logout window only",
                        theService);

                // Enables status notifications for friends traveling on foreign grids.
                // This is the same UserAgentService key HGFriendServerConnector already
                // requires in [HGFriendsService], so it is present on standard HG setups.
                theService = serverConfig.GetString("UserAgentService", string.Empty);
                if (theService.Length > 0)
                {
                    m_UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(theService, args);
                    if (m_UserAgentService == null)
                        m_log.WarnFormat("[HGFRIENDS SERVICE]: Could not load UserAgentService '{0}'. Traveling friend notifications disabled.", theService);
                }

                m_log.DebugFormat("[HGFRIENDS SERVICE]: Starting...");

            }
        }

        #region IHGFriendsService

        public int GetFriendPerms(UUID userID, UUID friendID)
        {
            FriendInfo[] friendsInfo = m_FriendsService.GetFriends(userID);
            foreach (FriendInfo finfo in friendsInfo)
            {
                if (finfo.Friend.StartsWith(friendID.ToString()))
                    return finfo.TheirFlags;
            }
            return -1;
        }

        public bool NewFriendship(FriendInfo friend, bool verified)
        {
            UUID friendID;
            string tmp = string.Empty, url = String.Empty, first = String.Empty, last = String.Empty;
            if (!Util.ParseUniversalUserIdentifier(friend.Friend, out friendID, out url, out first, out last, out tmp))
                return false;

            m_log.DebugFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} ({2})", friend.PrincipalID, friend.Friend, verified);

            // Does the friendship already exist?
            FriendInfo[] finfos = m_FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                if (finfo.Friend.StartsWith(friendID.ToString()))
                    return false;
            }
            // Verified user session. But the user needs to confirm friendship when he gets home
            if (verified)
                return m_FriendsService.StoreFriend(friend.PrincipalID.ToString(), friend.Friend, 0);

            // Does the reverted friendship exist? meaning that this user initiated the request
            finfos = m_FriendsService.GetFriends(friendID);
            bool userInitiatedOffer = false;
            foreach (FriendInfo finfo in finfos)
            {
                if (friend.Friend.StartsWith(finfo.PrincipalID.ToString()) && finfo.Friend.StartsWith(friend.PrincipalID.ToString()) && finfo.TheirFlags == -1)
                {
                    userInitiatedOffer = true;
                    // Let's delete the existing friendship relations that was stored
                    m_FriendsService.Delete(friendID, finfo.Friend);
                    break;
                }
            }

            if (userInitiatedOffer)
            {
                m_FriendsService.StoreFriend(friend.PrincipalID.ToString(), friend.Friend, 1);
                m_FriendsService.StoreFriend(friend.Friend, friend.PrincipalID.ToString(), 1);
                // notify the user
                ForwardToSim("ApproveFriendshipRequest", friendID, Util.UniversalName(first, last, url), "", friend.PrincipalID, "");
                return true;
            }
            return false;
        }

        public bool DeleteFriendship(FriendInfo friend, string secret)
        {
            FriendInfo[] finfos = m_FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                // We check the secret here. Or if the friendship request was initiated here, and was declined
                if (finfo.Friend.StartsWith(friend.Friend) && finfo.Friend.EndsWith(secret))
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: Delete friendship {0} {1}", friend.PrincipalID, friend.Friend);
                    m_FriendsService.Delete(friend.PrincipalID, finfo.Friend);
                    m_FriendsService.Delete(finfo.Friend, friend.PrincipalID.ToString());

                    return true;
                }
            }

            return false;
        }

        public bool FriendshipOffered(UUID fromID, string fromName, UUID toID, string message)
        {
            UserAccount account = m_UserAccountService.GetUserAccount(UUID.Zero, toID);
            if (account == null)
                return false;

            // OK, we have that user here.
            // So let's send back the call, but start a thread to continue
            // with the verification and the actual action.

            Util.FireAndForget(
                o => ProcessFriendshipOffered(fromID, fromName, toID, message), null, "HGFriendsService.ProcessFriendshipOffered");

            return true;
        }

        public bool ValidateFriendshipOffered(UUID fromID, UUID toID)
        {
            FriendInfo[] finfos = m_FriendsService.GetFriends(toID.ToString());
            foreach (FriendInfo fi in finfos)
            {
                if (fi.Friend.StartsWith(fromID.ToString()) && fi.TheirFlags == -1)
                    return true;
            }
            return false;
        }

        public List<UUID> StatusNotification(List<string> friends, UUID foreignUserID, bool online)
        {
            if (m_FriendsService == null || m_PresenceService == null)
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Unable to perform status notifications because friends or presence services are missing");
                return [];
            }

            // Let's unblock the caller right now, and take it from here async

            m_log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: foreign user {0} wants to notify {1} local friends of {2} status",
                foreignUserID, friends.Count, (online ? "online" : "offline"));

            // First, let's double check that the reported friends are, indeed, friends of that user
            // And let's check that the secret matches
            List<string> usersToBeNotified = [];
            HashSet<UUID> checkedUsers = [];
            string foreignUserIDToString = foreignUserID.ToString();
            foreach (string uui in friends)
            {
                if (Util.ParseUniversalUserIdentifier(uui, out UUID localUserID, out _, out _, out _, out string secret))
                {
                    // a bare UUID (or a UUI without secret) parses with an empty
                    // secret, and EndsWith("") always matches: without this check
                    // anyone could pass the friendship test for any local user
                    if (string.IsNullOrEmpty(secret))
                        continue;

                    // one friends lookup per local user, however often it is repeated
                    if (!checkedUsers.Add(localUserID))
                        continue;

                    FriendInfo[] friendInfos = m_FriendsService.GetFriends(localUserID);
                    foreach (FriendInfo finfo in friendInfos)
                    {
                        if (finfo.Friend.StartsWith(foreignUserIDToString) && finfo.Friend.EndsWith(secret))
                        {
                            // great!
                            usersToBeNotified.Add(localUserID.ToString());
                            break;
                        }
                    }
                }
            }

            //m_log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: user has {0} local friends", usersToBeNotified.Count);

            if (usersToBeNotified.Count == 0)
                return [];

            List<UUID> localFriendsOnline = [];

            // No early return when nobody has a home presence session: friends
            // traveling on foreign grids still need the traveling pass below.
            PresenceInfo[] friendSessions = m_PresenceService.GetAgents(usersToBeNotified.ToArray());
            if (friendSessions is not null && friendSessions.Length > 0)
            {
                foreach (PresenceInfo friendSession in friendSessions)
                {
                    // Guard against agents currently traveling between regions.
                    if (friendSession.RegionID.IsZero())
                        continue;

                    ForwardStatusNotificationToSim(
                        friendSession.RegionID,
                        foreignUserID,
                        friendSession.UserID,
                        online
                    );

                    //usersToBeNotified.Remove(friendSession.UserID.ToString());

                    if (online && UUID.TryParse(friendSession.UserID, out UUID id) && !localFriendsOnline.Contains(id))
                    {
                        localFriendsOnline.Add(id);
                    }
                }
            }

            // Notify local friends who are currently traveling on foreign grids.
            // Friendship was already confirmed above via the per-friend secret,
            // so no extra verification is needed here.
            if (m_UserAgentService != null)
            {
                // a traveler may still have a home presence entry and already be
                // in the list from the loop above; don't report them twice
                foreach (UUID id in TravelingFriendsNotifier.DeliverStatusToTravelers(
                        m_UserAgentService, usersToBeNotified, foreignUserID, online))
                {
                    if (online && !localFriendsOnline.Contains(id))
                        localFriendsOnline.Add(id);
                }
            }

            // and finally, let's send the online friends
            return online ? localFriendsOnline : [];
        }

        public void StatusNotifyTravelingFriends(UUID userID, UUID sessionID, List<string> friends, bool online)
        {
            if (m_FriendsService == null || m_UserAgentService == null)
                return;

            // auth capability: without this, anyone who knows two account UUIDs could
            // call this public endpoint directly to confirm a friendship + traveling
            // status, or inject spoofed online/offline events
            if (!IsOwnLiveSession(userID, sessionID, "statusnotify_traveling", offlineNotice: !online))
                return;

            // public, unauthenticated-by-session-key endpoint: confirm the claimed
            // friendship before delivering anything
            TravelingFriendsNotifier.DeliverStatusToTravelers(
                m_UserAgentService, friends, userID, online,
                id => TravelingFriendsNotifier.IsLocalFriend(m_FriendsService, id, userID));
        }

        public List<UUID> GetTravelingFriends(UUID userID, UUID sessionID, List<string> friends)
        {
            if (m_FriendsService == null || m_UserAgentService == null)
                return new List<UUID>();

            if (!IsOwnLiveSession(userID, sessionID, "gettravelingfriends"))
                return new List<UUID>();

            // this reveals the candidate's status to userID, so it is the
            // candidate's grant to userID that matters (not the reverse, which
            // is what IsLocalFriend checks for delivery)
            return TravelingFriendsNotifier.FilterTraveling(
                m_UserAgentService, friends,
                id => TravelingFriendsNotifier.LetsFriendSeeOnline(m_FriendsService, id, userID));
        }

        /// <summary>
        /// Confirms sessionID is a currently live Presence session belonging to userID
        /// on this grid (or one that just logged out). This stops callers who only know
        /// account UUIDs, but it is NOT strong authentication: the session ID is not a
        /// secret. It is sent in AgentCircuitData to every sim the user enters and to
        /// every foreign grid they visit, so any of those operators can present it.
        /// For grids that need more, the handler offers an IP allowlist
        /// (VerifyCallers / AuthorizedCallers in [HGFriendsService]); these methods are
        /// only ever called by this grid's own sims.
        ///
        /// Exposure compared with existing code: a session-ID holder gains nothing that
        /// is not already reachable without any secret. Status toasts can be injected
        /// directly through any sim's unauthenticated /friends/ statusnotification
        /// handler, and whether a user is traveling is returned by the UserAgentService's
        /// public locate_user.
        ///
        /// TODO: a real fix (service-to-service auth for grid-internal calls, a
        /// per-session token known only to the home grid, and authenticating the sim
        /// /friends/ handler) needs to cover those existing endpoints too, so it belongs
        /// in a separate change, not this feature.
        /// </summary>
        private static bool IsOwnLiveSession(UUID userID, UUID sessionID, string caller, bool offlineNotice = false)
        {
            if (sessionID.IsZero())
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: {0} for user {1} rejected: no session ID presented",
                    caller, userID);
                return false;
            }

            PresenceInfo p = m_PresenceService.GetAgent(sessionID);
            if (p != null && p.UserID == userID.ToString())
                return true;

            // Offline notifications are sent right after logout, and
            // PresenceService.LogoutAgent already deletes the session
            // synchronously on the sim's connection-close path — well before
            // this async request can arrive. Fall back to the short post-logout
            // grace window so genuine "I just went offline" calls still pass.
            if (m_PresenceService.WasRecentlyLoggedOut(sessionID, userID))
                return true;

            // That window lives in the process that handled the logout. On a split
            // Robust (sims log out through the private-port process, this service
            // runs in the public-port one) it is never set here, so fall back to the
            // shared GridUser record. It carries no session ID, so it is only good
            // for an "offline" notice: it can only confirm what is true (this user
            // did just log out), it never makes anyone look online.
            if (p == null && offlineNotice && JustLoggedOut(userID))
                return true;

            // rejections must be visible: a silent drop here looks identical to
            // "friend is offline" and is very hard to diagnose from the outside
            m_log.DebugFormat("[HGFRIENDS SERVICE]: {0} for user {1} rejected: session {2} is {3}",
                caller, userID, sessionID,
                p == null ? "not a live session nor a recent logout (expired grace window or a spoofed request)"
                          : "live but belongs to another user");
            return false;
        }

        private const int JUST_LOGGED_OUT_SECONDS = 60; // same window as PresenceService's recent-logout cache

        /// <summary>GridUser shows userID offline, with a logout in the last minute.</summary>
        private static bool JustLoggedOut(UUID userID)
        {
            if (m_GridUserService == null)
                return false;

            GridUserInfo info;
            try
            {
                // uncached: this process may hold the login-time copy (Online=true)
                // while the logout was written by another process
                info = m_GridUserService.GetGridUserInfoNoCache(userID.ToString());
            }
            catch (Exception e)
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: GridUser lookup for {0} failed: {1}", userID, e.Message);
                return false;
            }

            if (info == null || info.Online)
                return false;

            // small negative tolerance for clock skew between Robust hosts
            double age = (DateTime.UtcNow - info.Logout).TotalSeconds;
            return age >= -5 && age <= JUST_LOGGED_OUT_SECONDS;
        }

        #endregion IHGFriendsService

        #region Aux

        private void ProcessFriendshipOffered(UUID fromID, String fromName, UUID toID, String message)
        {
            // Great, it's a genuine request. Let's proceed.
            // But now we need to confirm that the requester is who he says he is
            // before we act on the friendship request.

            if (!fromName.Contains('@'))
                return;

            string[] parts = fromName.Split(new char[] {'@'});
            if (parts.Length != 2)
                return;

            string uriStr = "http://" + parts[1].ToLower();
            string SSLuriStr = "https://" + parts[1].ToLower();
            if(!Uri.TryCreate(uriStr, UriKind.Absolute, out _))
            {
                m_log.DebugFormat("[HGFRIENDS SERVICE]: Malformed address {0}", parts[1].ToLower());
                return;
            }

            UserAgentServiceConnector uasConn = new(uriStr);
            // If fail to connect with http... try with https...
            if (uasConn is null)
            {
                uasConn = new UserAgentServiceConnector(SSLuriStr);
                if (uasConn is null)
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: UserAgentServiceConnector failed to connect to {0}", parts[1].ToLower());
                    return;
                }
                uriStr = SSLuriStr;
            }
            
            Dictionary<string, object> servers = uasConn.GetServerURLs(fromID);
            if (!servers.TryGetValue("FriendsServerURI", out object friendsServerURI))
                return;

            HGFriendsServicesConnector friendsConn = new(friendsServerURI.ToString());
            if (!friendsConn.ValidateFriendshipOffered(fromID, toID))
            {
                m_log.WarnFormat("[HGFRIENDS SERVICE]: Friendship request from {0} to {1} is invalid. Impersonations?", fromID, toID);
                return;
            }

            string fromUUI = Util.UniversalIdentifier(fromID, parts[0], "@" + parts[1], uriStr);
            // OK, we're good!
            ForwardToSim("FriendshipOffered", fromID, fromName, fromUUI, toID, message);
        }

        private bool ForwardToSim(string op, UUID fromID, string name, String fromUUI, UUID toID, string message)
        {
            PresenceInfo session = null;
            GridRegion region = null;
            PresenceInfo[] sessions = m_PresenceService.GetAgents(new string[] { toID.ToString() });
            if (sessions != null && sessions.Length > 0)
                session = sessions[0];
            if (session != null)
                region = m_GridService.GetRegionByUUID(UUID.Zero, session.RegionID);

            switch (op)
            {
                case "FriendshipOffered":
                    // Let's store backwards
                    string secret = UUID.Random().ToString().Substring(0, 8);
                    m_FriendsService.StoreFriend(toID.ToString(), fromUUI + ";" + secret, 0);
                    if (m_FriendsLocalSimConnector != null) // standalone
                    {
                        GridInstantMessage im = new GridInstantMessage(null, fromID, name, toID,
                            (byte)InstantMessageDialog.FriendshipOffered, message, false, Vector3.Zero);
                        // !! HACK
                        im.imSessionID = im.fromAgentID;
                        return m_FriendsLocalSimConnector.LocalFriendshipOffered(toID, im);
                    }
                    else if (region != null) // grid
                        return m_FriendsSimConnector.FriendshipOffered(region, fromID, toID, message, name);
                    break;
                case "ApproveFriendshipRequest":
                    if (m_FriendsLocalSimConnector != null) // standalone
                        return m_FriendsLocalSimConnector.LocalFriendshipApproved(fromID, name, toID);
                    else if (region != null) //grid
                        return m_FriendsSimConnector.FriendshipApproved(region, fromID, name, toID);
                    break;
            }

            return false;
        }

        protected void ForwardStatusNotificationToSim(UUID regionID, UUID foreignUserID, string user, bool online)
        {
            if (UUID.TryParse(user, out UUID userID))
            {
                if (m_FriendsLocalSimConnector != null)
                {
                    m_log.DebugFormat("[HGFRIENDS SERVICE]: Local Notify, user {0} is {1}", foreignUserID, (online ? "online" : "offline"));
                    m_FriendsLocalSimConnector.StatusNotify(foreignUserID, userID, online);
                }
                else
                {
                    GridRegion region = m_GridService.GetRegionByUUID(UUID.Zero /* !!! */, regionID);
                    if (region != null)
                    {
                        m_log.DebugFormat("[HGFRIENDS SERVICE]: Remote Notify to region {0}, user {1} is {2}", region.RegionName, foreignUserID, (online ? "online" : "offline"));
                        m_FriendsSimConnector.StatusNotify(region, foreignUserID, userID.ToString(), online);
                    }
                }
            }
        }

        #endregion Aux
    }
}
