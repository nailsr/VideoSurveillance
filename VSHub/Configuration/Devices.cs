using System;
using System.Collections.Generic;
using System.Text;
using System.Configuration;

namespace VSHub.Configuration
{
    public class Devices : ConfigurationElementCollection
    {
        public Devices()
        {
        }

        public override ConfigurationElementCollectionType CollectionType
        {
            get
            {
                return ConfigurationElementCollectionType.AddRemoveClearMap;
            }
        }

        protected override ConfigurationElement CreateNewElement()
        {
            return new Device();
        }

        protected override Object GetElementKey(ConfigurationElement element)
        {
            return ((Device)element).Name;
        }

        public Device this[int index]
        {
            get
            {
                return (Device)BaseGet(index);
            }
            set
            {
                if (BaseGet(index) != null)
                {
                    BaseRemoveAt(index);
                }
                BaseAdd(index, value);
            }
        }

        new public Device this[string Name]
        {
            get
            {
                return (Device)BaseGet(Name);
            }
        }

        public int IndexOf(Device device)
        {
            return BaseIndexOf(device);
        }

        public void Add(Device device)
        {
            BaseAdd(device);
        }
        protected override void BaseAdd(ConfigurationElement element)
        {
            BaseAdd(element, false);
        }

        public void Remove(Device device)
        {
            if (BaseIndexOf(device) >= 0)
                BaseRemove(device.Name);
        }

        public void RemoveAt(int index)
        {
            BaseRemoveAt(index);
        }

        public void Remove(string name)
        {
            BaseRemove(name);
        }

        public void Clear()
        {
            BaseClear();
        }
    }

    public class Device : ConfigurationElement
    {
        [ConfigurationProperty("name", DefaultValue = "", IsKey = true, IsRequired = true)]
        public String Name
        {
            get
            {
                return (String)this["name"];
            }
            set
            {
                this["name"] = value;
            }
        }

        [ConfigurationProperty("ipaddr")]
        public string IPAddr
        {
            get
            {
                return (string)this["ipaddr"];
            }
            set
            {
                this["ipaddr"] = value;
            }
        }

        [ConfigurationProperty("port")]
        public int Port
        {
            get
            {
                return (int)this["port"];
            }
            set
            {
                this["port"] = value;
            }
        }

        [ConfigurationProperty("login")]
        public String Login
        {
            get
            {
                return (String)this["login"];
            }
            set
            {
                this["login"] = value;
            }
        }

        [ConfigurationProperty("password")]
        public String Password
        {
            get
            {
                return (String)this["password"];
            }
            set
            {
                this["password"] = value;
            }
        }

        [ConfigurationProperty("channels")]
        public int NumberOfChannels
        {
            get
            {
                return (int)(this["channels"]);
            }
            set
            {
                this["channels"] = value.ToString();
            }
        }
    }
}
