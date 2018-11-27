using System;
using System.Collections.Generic;
using System.Text;
using System.Configuration;
using System.Xml;

namespace VSHub.Configuration
{
    public class Sources : ConfigurationElementCollection
    {
        public Sources()
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
            return new Source();
        }

        protected override Object GetElementKey(ConfigurationElement element)
        {
            return ((Source)element).Name;
        }

        public Source this[int index]
        {
            get
            {
                return (Source)BaseGet(index);
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

        new public Source this[string Name]
        {
            get
            {
                return (Source)BaseGet(Name);
            }
        }

        public int IndexOf(Source source)
        {
            return BaseIndexOf(source);
        }

        public void Add(Source source)
        {
            BaseAdd(source);
        }
        protected override void BaseAdd(ConfigurationElement element)
        {
            BaseAdd(element, false);
        }

        public void Remove(Source source)
        {
            if (BaseIndexOf(source) >= 0)
                BaseRemove(source.Name);
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

    public class Source : ConfigurationElement
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

        [ConfigurationProperty("description", DefaultValue = "")]
        public String Description
        {
            get
            {
                var description = (String)this["description"];

                return string.IsNullOrWhiteSpace(description) ? this.Name : description;
            }
            set
            {
                this["description"] = value;
            }
        }

        [ConfigurationProperty("device")]
        public string DeviceName
        {
            get
            {
                return (string)(this["device"]);
            }
            set
            {
                this["device"] = value;
            }
        }

        [ConfigurationProperty("channel")]
        public int DeviceChannel
        {
            get
            {
                return (int)(this["channel"]);
            }
            set
            {
                this["channel"] = value;
            }
        }

        [ConfigurationProperty("format")]
        public string Format
        {
            get
            {
                return (string)this["format"];
            }
            set
            {
                this["format"] = value;
            }
        }

        [ConfigurationProperty("", DefaultValue = null, IsDefaultCollection = true)]
        [ConfigurationCollection(typeof(VSHub.Configuration.ACL))]
        public ACL AccessRights { get { return (ACL)base[""]; } }
    }


    public class ACL : ConfigurationElementCollection
    {
        public ACL()
        {
        }

        protected override ConfigurationElement CreateNewElement(string elementName)
        {
            return new AccessRights() { Verb = elementName };
        }

        public override ConfigurationElementCollectionType CollectionType
        {
            get
            {
                return ConfigurationElementCollectionType.BasicMap;
            }
        }

        protected override bool IsElementName(string elementName)
        {
            if (string.Compare(elementName, "allow", true) == 0 || string.Compare(elementName, "deny", true) == 0) return true;

            return base.IsElementName(elementName);
        }

        protected override ConfigurationElement CreateNewElement()
        {
            return new AccessRights();
        }

        public AccessRights this[int index]
        {
            get
            {
                return (AccessRights)BaseGet(index);
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

        new public AccessRights this[string Name]
        {
            get
            {
                return (AccessRights)BaseGet(Name);
            }
        }

        public int IndexOf(AccessRights source)
        {
            return BaseIndexOf(source);
        }

        public void Add(AccessRights source)
        {
            BaseAdd(source);
        }
        protected override void BaseAdd(ConfigurationElement element)
        {
            BaseAdd(element, false);
        }

        protected override object GetElementKey(ConfigurationElement element)
        {
            return (element as AccessRights).Verb + ":" + (element as AccessRights).Users;
        }
    }

    public class AccessRights : ConfigurationElement
    {
        public String Verb
        {
            get;
            set;
        }

        [ConfigurationProperty("users")]
        public string Users
        {
            get
            {
                return (string)(this["users"]);
            }
            set
            {
                this["users"] = value;
            }
        }        
    }
}
