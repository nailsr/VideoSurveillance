using System;
using System.Collections.Generic;
using System.Text;
using System.Configuration;

namespace VSHub.Configuration
{
    public class Users : ConfigurationElementCollection
    {
        public Users()
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
            return new User();
        }

        protected override Object GetElementKey(ConfigurationElement element)
        {
            return ((User)element).Name;
        }

        public User this[int index]
        {
            get
            {
                return (User)BaseGet(index);
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

        new public User this[string Name]
        {
            get
            {
                return (User)BaseGet(Name);
            }
        }

        public int IndexOf(User user)
        {
            return BaseIndexOf(user);
        }

        public void Add(User user)
        {
            BaseAdd(user);
        }
        protected override void BaseAdd(ConfigurationElement element)
        {
            BaseAdd(element, false);
        }

        public void Remove(User user)
        {
            if (BaseIndexOf(user) >= 0)
                BaseRemove(user.Name);
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

    public class User : ConfigurationElement
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

        [ConfigurationProperty("password", DefaultValue = "", IsRequired = true)]
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
    }
}
