﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VSHub
{
    public class Session
    {
        public string ID;

        public List<Channel> OpenedChannels = new List<Channel>();

        public string UserName;

        public Session(string sessionID, string user)
        {
            this.ID = sessionID;
            this.UserName = user;
        }

        public DateTime LastAccess;


    }
}