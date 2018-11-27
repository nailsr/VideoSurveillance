using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace VSHub.Configuration
{
    public class VSHubConfigurationSectionHandler : ConfigurationSection
    {
        [ConfigurationProperty("users")]
        [ConfigurationCollection(typeof(VSHub.Configuration.Users),
            AddItemName = "user",
            ClearItemsName = "clear",
            RemoveItemName = "remove")]
        public Users Users
        {
            get
            {
                return (Users)base["users"];
            }
        }

        [ConfigurationProperty("devices")]
        [ConfigurationCollection(typeof(VSHub.Configuration.Devices),
            AddItemName = "device",
            ClearItemsName = "clear",
            RemoveItemName = "remove")]
        public Devices Devices
        {
            get
            {
                return (Devices)base["devices"];
            }
        }

        [ConfigurationProperty("sources")]
        [ConfigurationCollection(typeof(VSHub.Configuration.Sources),
            AddItemName = "source",
            ClearItemsName = "clear",
            RemoveItemName = "remove")]
        public Sources Sources
        {
            get
            {
                return (Sources)base["sources"];
            }
        }

        [ConfigurationProperty("prefix")]
        public string Prefix
        {
            get
            {
                return (string)base["prefix"];
            }
        }

        [ConfigurationProperty("sessionTimeout")]
        public TimeSpan SessionTimeout
        {
            get
            {
                return (TimeSpan)base["sessionTimeout"];
            }
        }
    }
}
