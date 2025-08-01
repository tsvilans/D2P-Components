﻿using D2P_Core.Interfaces;
using D2P_Core.Utility;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace D2P_Core
{
    using AttributeCollection = Dictionary<Guid, ObjectAttributes>;
    using GeometryCollection = Dictionary<Guid, GeometryBase>;
    using LayerCollection = Dictionary<ILayerInfo, Dictionary<Guid, int>>;

    public class Component : IComponent
    {

        GeometryCollection _geometryCollection = new GeometryCollection();
        AttributeCollection _attributeCollection = new AttributeCollection();
        LayerCollection _stagingLayerCollection = new LayerCollection();

        protected ComponentType _componentType;

        // Doc
        public RhinoDoc ActiveDoc { get; set; } = RhinoDoc.ActiveDoc;

        // IDs
        public Guid ID { get; set; }
        public int GroupIdx => Utility.Group.GetGroupIndex(ID, ActiveDoc);
        public string Name => TypeID + Settings.TypeDelimiter + ShortName;
        public string ShortName => Label.PlainText;

        // State
        public bool IsInitialized => ActiveDoc.Layers.FindName(Layers.ComposeComponentTypeLayerName(this)) != null;
        public bool IsVirtual => ActiveDoc.Objects.FindId(ID) == null;
        public bool IsVirtualClone { get; private set; }

        // Type
        public ComponentType ComponentType => _componentType;
        public string TypeID => _componentType.TypeID;
        public string TypeName => _componentType.TypeName;
        public Color LayerColor => _componentType.LayerColor;
        public Settings Settings => _componentType.Settings;

        // Geometry
        public double LabelSize => Label.TextHeight;
        public TextEntity Label
        {
            get
            {
                GeometryCollection.TryGetValue(ID, out var value);
                return value as TextEntity;
            }
        }
        public Plane Plane
        {
            get => Label.Plane;
            set => (GeometryCollection[ID] as TextEntity).Plane = value;

        }
        public GeometryCollection GeometryCollection
        {
            get => IsVirtualClone || IsVirtual || _geometryCollection.Any() ? _geometryCollection : RHObjects.ToDictionary(rh => rh.Id, rh => rh.Geometry);
            private set => _geometryCollection = value.ToDictionary(kv => kv.Key, kv => kv.Value.Duplicate());
        }
        public AttributeCollection AttributeCollection
        {
            get => IsVirtualClone || IsVirtual || _attributeCollection.Any() ? _attributeCollection : RHObjects.ToDictionary(rh => rh.Id, rh => rh.Attributes);
            private set => _attributeCollection = value.ToDictionary(kv => kv.Key, kv => kv.Value.Duplicate());
        }
        public LayerCollection StagingLayerCollection { get => _stagingLayerCollection; private set => _stagingLayerCollection = value; }
        public IEnumerable<RhinoObject> RHObjects => Objects.ObjectsByGroup(GroupIdx, ActiveDoc);
        public IEnumerable<GeometryBase> Geometry => GeometryCollection.Values;
        public IEnumerable<ObjectAttributes> Attributes => AttributeCollection.Values;

        public Component(ComponentType componentType, string name, Plane plane)
        {
            ID = Guid.NewGuid();
            _componentType = componentType;
            var label = TextEntity.Create(name, plane, Settings.DimensionStyle, false, 0, 0);
            label.TextHeight = componentType.LabelSize;
            GeometryCollection.Add(ID, label);
            StagingLayerCollection.Add(new LayerInfo(), new Dictionary<Guid, int>() { { ID, -1 } });
            AttributeCollection.Add(ID, new ObjectAttributes() { Name = Name, LayerIndex = 0 });
        }

        public Component(ComponentType componentType, Guid id, RhinoDoc doc = null)
        {
            doc = doc ?? RhinoDoc.ActiveDoc;
            ID = id;
            _componentType = componentType;
            ActiveDoc = doc;
        }

        protected Component(IComponent component)
        {
            ID = component.ID;
            _componentType = new ComponentType(component);
            GeometryCollection = new GeometryCollection(component.GeometryCollection);
            var label = TextEntity.Create(ShortName, Plane, Settings.DimensionStyle, false, 0, 0);
            label.TextHeight = _componentType.LabelSize;
            GeometryCollection[ID] = label;
            AttributeCollection = new AttributeCollection(component.AttributeCollection);
            StagingLayerCollection = new LayerCollection(component.StagingLayerCollection);
            IsVirtualClone = component.IsVirtualClone;
        }

        public bool Transform(Transform xform) => Geometry.Select(geo => geo.Transform(xform)).All(r => r);
        public IComponent VirtualClone() => new Component(this) { IsVirtualClone = true };
        public IComponent Clone() => new Component(this);

        public IList<Guid> AddMember(ComponentMember member)
        {
            if (member == null) return new List<Guid>();

            CacheCollections();

            if (!StagingLayerCollection.ContainsKey(member.LayerInfo))
                StagingLayerCollection.Add(member.LayerInfo, new Dictionary<Guid, int>());

            return AddObjectToCollections(member);
        }

        public IList<Guid> ReplaceMember(ComponentMember member)
        {
            if (member == null) return new List<Guid>();

            CacheCollections();

            if (StagingLayerCollection.TryGetValue(member.LayerInfo, out Dictionary<Guid, int> value))
            {
                foreach (var objID in value.Select(kv => kv.Key))
                {
                    RemoveObjectFromCollections(objID);
                }
            }
            else StagingLayerCollection.Add(member.LayerInfo, new Dictionary<Guid, int>());

            var layerIdx = Layers.FindLayerIndexByFullPath(this, member.LayerInfo.RawLayerName);
            if (layerIdx > 0)
            {
                foreach (var objID in Objects.ObjectIDsByLayer(this, layerIdx))
                {
                    if (Guid.Empty == objID) continue;
                    RemoveObjectFromCollections(objID);
                }
            }

            return AddObjectToCollections(member);
        }

        IList<Guid> AddObjectToCollections(ComponentMember member)
        {
            var layerIdx = Layers.FindLayerIndexByFullPath(this, member.LayerInfo.RawLayerName);
            var objectAttributes = member.ObjectAttributes ?? new ObjectAttributes();
            objectAttributes.Name = Name;
            if (layerIdx > 0)
                objectAttributes.LayerIndex = layerIdx;

            var ids = new List<Guid>();
            foreach (var geometry in member.GeometryBases)
            {
                if (geometry == null)
                    continue;
                var newID = Guid.NewGuid();
                ids.Add(newID);
                if (layerIdx <= 0)
                    StagingLayerCollection[member.LayerInfo][newID] = -1;
                GeometryCollection.Add(newID, geometry);
                AttributeCollection.Add(newID, objectAttributes);
            }

            return ids;
        }

        void CacheCollections()
        {
            _geometryCollection = GeometryCollection;
            _attributeCollection = AttributeCollection;
        }
        void RemoveObjectFromCollections(Guid objID)
        {
            _geometryCollection.Remove(objID);
            _attributeCollection.Remove(objID);
        }

        public void ClearStagingLayerCollection()
        {
            foreach (var objID in StagingLayerCollection.Values.SelectMany(x => x.Keys))
            {
                RemoveObjectFromCollections(objID);
            }
            StagingLayerCollection.Clear();
        }
    }
}