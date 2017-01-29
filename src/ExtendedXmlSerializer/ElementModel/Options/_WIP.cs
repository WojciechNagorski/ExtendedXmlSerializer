﻿// MIT License
// 
// Copyright (c) 2016 Wojciech Nagórski
//                    Michael DeMond
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using ExtendedXmlSerialization.Conversion;
using ExtendedXmlSerialization.Conversion.Xml;
using ExtendedXmlSerialization.Conversion.Xml.Converters;
using ExtendedXmlSerialization.Core;
using ExtendedXmlSerialization.Core.Sources;
using ExtendedXmlSerialization.Core.Specifications;
using ExtendedXmlSerialization.ElementModel.Members;
using ExtendedXmlSerialization.ElementModel.Names;
using ExtendedXmlSerialization.TypeModel;

namespace ExtendedXmlSerialization.ElementModel.Options
{
	public interface IElementContext : IClassification
	{
		void Emit(IEmitter emitter, object instance);

		object Yield(IYielder yielder);
	}

	public interface IEmitter : IServiceProvider
	{
		IDisposable Emit(IName name);

		void Write(string text);
	}

	public class XmlEmitter : IEmitter
	{
		readonly XmlWriter _writer;
		readonly INamespaces _namespaces;
		readonly IDisposable _finish;

		public XmlEmitter(INamespaces namespaces, XmlWriter writer)
			: this(namespaces, writer, new DelegatedDisposable(writer.WriteEndElement)) {}

		public XmlEmitter(INamespaces namespaces, XmlWriter writer, IDisposable finish)
		{
			_writer = writer;
			_namespaces = namespaces;
			_finish = finish;
		}

		public object GetService(Type serviceType)
		{
			throw new NotImplementedException();
		}

		public IDisposable Emit(IName name)
		{
			if (name is IMemberName)
			{
				_writer.WriteStartElement(name.DisplayName);

				/*var type = instance.GetType().GetTypeInfo();
				if (!context.Container.Exact(type))
				{
					context.Write(TypeProperty.Default, type);
				}*/
			}
			else
			{
				_writer.WriteStartElement(name.DisplayName, _namespaces.Get(name.Classification).Namespace.NamespaceName);
			}
			return _finish;
		}

		public void Write(string text) => _writer.WriteString(text);
	}

	public interface IContextOption : IOption<TypeInfo, IElementContext> {}

	public class Serializer : CacheBase<TypeInfo, IElementContext>, IExtendedXmlSerializer
	{
		readonly INames _names;
		readonly IContexts _contexts;
		readonly INamespaces _namespaces;
		readonly ITypeYielder _type;
		public Serializer() : this(new Names(), new Namespaces()) {}

		public Serializer(INames names, INamespaces namespaces)
			: this(names, new Contexts(names), namespaces, new TypeYielder(new Types(namespaces, new TypeContexts()))) {}

		public Serializer(INames names, IContexts contexts, INamespaces namespaces, ITypeYielder type)
		{
			_names = names;
			_contexts = contexts;
			_namespaces = namespaces;
			_type = type;
		}

		public void Serialize(Stream stream, object instance)
		{
			using (var writer = XmlWriter.Create(stream))
			{
				var emitter = new XmlEmitter(_namespaces, writer);
				var context = Get(instance.GetType().GetTypeInfo());
				context.Emit(emitter, instance);
			}
		}

		public object Deserialize(Stream stream)
		{
			using (var reader = XmlReader.Create(stream))
			{
				var yielder = new XmlYielder(_type, reader);
				var classification = yielder.Classification();
				var context = Get(classification);
				var result = context.Yield(yielder);
				return result;
			}
		}

		protected override IElementContext Create(TypeInfo parameter)
			=> new RootContext(_names.Get(parameter), _contexts.Get(parameter));
	}

	public interface IYielder
	{
		TypeInfo Classification();

		string Value();
	}

	class XmlYielder : IYielder
	{
		readonly ITypeYielder _type;
		readonly XmlReader _reader;

		public XmlYielder(ITypeYielder type, XmlReader reader)
		{
			_type = type;
			_reader = reader;
		}

		public TypeInfo Classification() => _type.Get(_reader);

		public string Value() => _reader.ReadElementContentAsString();
	}

	public interface ITypeYielder : IParameterizedSource<XmlReader, TypeInfo> {}

	class TypeYielder : ITypeYielder
	{
		readonly ITypes _types;

		public TypeYielder(ITypes types)
		{
			_types = types;
		}

		public TypeInfo Get(XmlReader parameter)
		{
			switch (parameter.MoveToContent())
			{
				case XmlNodeType.Element:
					var name = XName.Get(parameter.LocalName, parameter.NamespaceURI);
					var result = _types.Get(name);
					return result;
			}

			throw new InvalidOperationException($"Could not locate the type from the current Xml reader '{parameter}.'");
		}
	}

	public class RootContext : NamedContext
	{
		public RootContext(IName name, IElementContext body) : base(name, body) {}
	}

	public class Contexts : IContexts
	{
		readonly IParameterizedSource<TypeInfo, IElementContext> _source;

		public Contexts(INames names)
		{
			_source = new Selector<TypeInfo, IElementContext>(new ContextOptions(this, names).ToArray());
		}

		public IElementContext Get(TypeInfo parameter) => _source.Get(parameter);
	}

	public interface INames : ISelector<TypeInfo, IName> {}

	public interface IContextOptions : IEnumerable<IContextOption> {}

	class ContextOptions : IContextOptions
	{
		readonly static MemberSpecification<FieldInfo> Field =
			new MemberSpecification<FieldInfo>(FieldMemberSpecification.Default);

		readonly static MemberSpecification<PropertyInfo> Property =
			new MemberSpecification<PropertyInfo>(PropertyMemberSpecification.Default);

		readonly IContexts _contexts;
		readonly INames _names;
		readonly IActivators _activators;
		readonly ICollectionItemTypeLocator _locator;
		readonly IAddDelegates _add;
		readonly ISpecification<PropertyInfo> _property;
		readonly ISpecification<FieldInfo> _field;

		public ContextOptions(IContexts contexts, INames names)
			: this(contexts, names, new Activators(), new CollectionItemTypeLocator(), new AddMethodLocator()) {}

		public ContextOptions(IContexts contexts, INames names, IActivators activators, ICollectionItemTypeLocator locator,
		                      IAddMethodLocator add)
			: this(contexts, names, activators, locator, new AddDelegates(locator, add), Property, Field) {}

		public ContextOptions(IContexts contexts, INames names, IActivators activators, ICollectionItemTypeLocator locator,
		                      IAddDelegates add, ISpecification<PropertyInfo> property, ISpecification<FieldInfo> field)
		{
			_contexts = contexts;
			_names = names;
			_activators = activators;
			_locator = locator;
			_add = add;
			_property = property;
			_field = field;
		}

		public IEnumerator<IContextOption> GetEnumerator()
		{
			yield return BooleanTypeConverter.Default;
			yield return CharacterTypeConverter.Default;
			yield return ByteTypeConverter.Default;
			yield return UnsignedByteTypeConverter.Default;
			yield return ShortTypeConverter.Default;
			yield return UnsignedShortTypeConverter.Default;
			yield return IntegerTypeConverter.Default;
			yield return UnsignedIntegerTypeConverter.Default;
			yield return LongTypeConverter.Default;
			yield return UnsignedLongTypeConverter.Default;
			yield return FloatTypeConverter.Default;
			yield return DoubleTypeConverter.Default;
			yield return DecimalTypeConverter.Default;
			yield return EnumerationTypeConverter.Default;
			yield return DateTimeTypeConverter.Default;
			yield return DateTimeOffsetTypeConverter.Default;
			yield return StringTypeConverter.Default;
			yield return GuidTypeConverter.Default;
			yield return TimeSpanTypeConverter.Default;

			// yield return new DictionaryContext();
			yield return new CollectionContextOption(_contexts, _activators, _locator, _names, _add);

			var members = new ContextMembers(new MemberContextSelector(_contexts, _add), _property, _field);
			yield return new MemberedContextOption(members);
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public interface IContexts : IParameterizedSource<TypeInfo, IElementContext> {}

	class Names : INames
	{
		readonly IParameterizedSource<TypeInfo, IName> _selector;
		public Names() : this(ElementModel.Names.Defaults.Names) {}

		public Names(ImmutableArray<IName> names)
		{
			_selector = new Selector<TypeInfo, IName>(new KnownNamesOption(names), new GenericNameOption(this),
			                                          NameOption.Default);
		}

		public IName Get(TypeInfo parameter) => _selector.Get(parameter);
	}

	abstract class ContainerContextOptionBase<T> : ContextOptionBase where T : IName
	{
		readonly INameProvider<T> _name;

		protected ContainerContextOptionBase(ISpecification<TypeInfo> specification, INameProvider<T> name)
			: base(specification)
		{
			_name = name;
		}

		public override IElementContext Get(TypeInfo parameter) => Create(_name.Get(parameter));

		protected abstract IElementContext Create(T name);
	}

	public abstract class ContextOptionBase : OptionBase<TypeInfo, IElementContext>, IContextOption
	{
		protected ContextOptionBase(ISpecification<TypeInfo> specification) : base(specification) {}
	}


	public class FixedContextOption : FixedOption<TypeInfo, IElementContext>, IContextOption
	{
		public FixedContextOption(ISpecification<TypeInfo> specification, IElementContext context)
			: base(specification, context) {}
	}

	class ValueContext<T> : ContextBase<T>
	{
		readonly static TypeInfo Type = typeof(T).GetTypeInfo();

		readonly Func<string, T> _deserialize;
		readonly Func<T, string> _serialize;

		public ValueContext(Func<string, T> deserialize, Func<T, string> serialize) : base(Type)
		{
			_deserialize = deserialize;
			_serialize = serialize;
		}

		public override void Emit(IEmitter emitter, T instance) => emitter.Write(_serialize(instance));

		public override object Yield(IYielder yielder) => _deserialize(yielder.Value());
	}

	public interface IContextMembers : IParameterizedSource<TypeInfo, IMembers> {}

	public interface IMembers : IEnumerable<IMemberContext>, IParameterizedSource<string, IMemberContext> {}

	public interface IMemberContext : IElementContext, IName {}

	class CollectionContextOption : ContainerContextOptionBase<IName>
	{
		readonly IContexts _contexts;
		readonly IActivators _activators;
		readonly IAddDelegates _add;

		public CollectionContextOption(IContexts contexts, IActivators activators, ICollectionItemTypeLocator locator,
		                               INames names, IAddDelegates add)
			: this(contexts, new CollectionItemNameProvider(locator, names), activators, add) {}

		public CollectionContextOption(IContexts contexts, INameProvider names, IActivators activators,
		                               IAddDelegates add) : base(IsCollectionTypeSpecification.Default, names)
		{
			_contexts = contexts;
			_activators = activators;
			_add = add;
		}

		protected override IElementContext Create(IName name)
			=> new EnumerableContext(new CollectionItemContext(_contexts, name), _activators, _add);
	}

	/*class DictionaryContextOption : ContainerContextOptionBase<ICollectionName>
	{
		readonly IContexts _contexts;
		readonly IActivators _activators;
		readonly IAddDelegates _add;

		public DictionaryContextOption(IContexts contexts, IActivators activators, ICollectionItemTypeLocator locator,
		                               INames names, IAddDelegates add)
			: this(contexts, new CollectionNameProvider(locator, names), activators, add) {}

		public DictionaryContextOption(IContexts contexts, INameProvider<ICollectionName> names, IActivators activators,
		                               IAddDelegates add)
			: base(IsCollectionTypeSpecification.Default, names)
		{
			_contexts = contexts;
			_activators = activators;
			_add = add;
		}

		protected override IElementContext Create(ICollectionName name)
			=> new EnumerableContext(new CollectionItemContext(_contexts, name), _activators, _add);
	}*/

	class CollectionItemNameProvider : NameProviderBase
	{
		readonly INames _names;
		readonly ICollectionItemTypeLocator _locator;

		public CollectionItemNameProvider(ICollectionItemTypeLocator locator, INames names)
			: this(locator, new EnumerableTypeFormatter(locator))
		{
			_names = names;
		}

		public CollectionItemNameProvider(ICollectionItemTypeLocator locator, ITypeFormatter formatter) : base(formatter)
		{
			_locator = locator;
		}

		public override IName Create(string displayName, TypeInfo classification) => _names.Get(_locator.Get(classification));
	}

	class CollectionItemContext : NamedContext
	{
		readonly IContexts _contexts;

		public CollectionItemContext(IContexts contexts, IName elementName)
			: this(contexts, elementName, contexts.Get(elementName.Classification)) {}

		public CollectionItemContext(IContexts contexts, IName elementName, IElementContext body) : base(elementName, body)
		{
			_contexts = contexts;
		}

		public override void Emit(IEmitter emitter, object instance)
		{
			var type = instance.GetType();
			var actual = type.GetTypeInfo();
			if (Equals(actual, Name.Classification))
			{
				base.Emit(emitter, instance);
			}
			else
			{
				_contexts.Get(actual).Emit(emitter, instance);
			}
		}
	}

	class DictionaryContext : EnumerableContext<IDictionary>
	{
		public DictionaryContext(IElementContext item, IActivators activators, IAddDelegates add, TypeInfo classification)
			: base(item, activators, add, classification) {}

		protected override IEnumerator Get(IDictionary instance) => instance.GetEnumerator();
	}

	class EnumerableContext : EnumerableContext<IEnumerable>
	{
		public EnumerableContext(IElementContext item, IActivators activators, IAddDelegates add)
			: base(item, activators, add) {}
	}

	class EnumerableContext<T> : ContextBase<T> where T : IEnumerable
	{
		readonly IElementContext _item;
		readonly IActivators _activators;
		readonly IAddDelegates _add;

		public EnumerableContext(IElementContext item, IActivators activators, IAddDelegates add)
			: this(item, activators, add, item.Classification) {}

		public EnumerableContext(IElementContext item, IActivators activators, IAddDelegates add, TypeInfo classification)
			: base(classification)
		{
			_item = item;
			_activators = activators;
			_add = add;
		}

		protected virtual IEnumerator Get(T instance) => instance.GetEnumerator();

		public override void Emit(IEmitter emitter, T instance)
		{
			var enumerator = Get(instance);
			while (enumerator.MoveNext())
			{
				_item.Emit(emitter, enumerator.Current);
			}
		}

		public override object Yield(IYielder yielder)
		{
			throw new NotImplementedException();
		}
	}

	class MemberedContextOption : ContextOptionBase
	{
		readonly IContextMembers _contexts;

		public MemberedContextOption(IContextMembers contexts) : base(IsActivatedTypeSpecification.Default)
		{
			_contexts = contexts;
		}

		public override IElementContext Get(TypeInfo parameter) => new MemberedContext(_contexts, parameter);
	}

	class MemberedContext : ContextBase
	{
		readonly IContextMembers _contexts;
		readonly IMembers _members;

		public MemberedContext(IContextMembers contexts, TypeInfo classification)
			: this(contexts, contexts.Get(classification), classification) {}

		public MemberedContext(IContextMembers contexts, IMembers members, TypeInfo classification) : base(classification)
		{
			_contexts = contexts;
			_members = members;
		}

		public override void Emit(IEmitter emitter, object instance)
		{
			var type = instance.GetType();
			var typeInfo = type.GetTypeInfo();
			var same = type == Classification.AsType();
			var members = same ? _members : _contexts.Get(typeInfo);
			foreach (var member in members)
			{
				member.Emit(emitter, instance);
			}
		}

		public override object Yield(IYielder yielder)
		{
			throw new NotImplementedException();
		}
	}

	public class Members : IMembers
	{
		readonly ImmutableArray<IMemberContext> _members;
		readonly IDictionary<string, IMemberContext> _lookup;

		/*public Members(TypeInfo classification, params IMember[] members) : this(classification, members.AsEnumerable()) {}*/

		public Members(IEnumerable<IMemberContext> members) : this(members.ToImmutableArray()) {}

		public Members(ImmutableArray<IMemberContext> members) : this(members, members.ToDictionary(x => x.DisplayName)) {}

		public Members(ImmutableArray<IMemberContext> members, IDictionary<string, IMemberContext> lookup)
		{
			_members = members;
			_lookup = lookup;
		}

		public IMemberContext Get(string parameter) => _lookup.TryGet(parameter);

		public IEnumerator<IMemberContext> GetEnumerator()
		{
			foreach (var element in _members)
			{
				yield return element;
			}
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public class ReadOnlyCollectionMemberOption : MemberOptionBase
	{
		readonly IContexts _contexts;
		readonly IGetterFactory _getter;
		readonly IAddDelegates _add;

		public ReadOnlyCollectionMemberOption(IContexts contexts, IAddDelegates add)
			: this(contexts, GetterFactory.Default, add) {}

		public ReadOnlyCollectionMemberOption(IContexts contexts, IGetterFactory getter, IAddDelegates add)
			: base(Specification.Instance)
		{
			_contexts = contexts;
			_getter = getter;
			_add = add;
		}

		protected override IMemberContext Create(IMemberName name)
		{
			var add = _add.Get(name.MemberType);
			if (add != null)
			{
				var getter = _getter.Get(name.Metadata);
				var result = new ReadOnlyCollectionMember(name, add, getter, _contexts.Get(name.MemberType));
				return result;
			}
			return null;
		}

		public class ReadOnlyCollectionMember : MemberContext
		{
			public ReadOnlyCollectionMember(IMemberName name, Action<object, object> add, Func<object, object> getter,
			                                IElementContext context)
				: base(name, context, add, getter) {}

			protected override void Assign(object instance, object value)
			{
				var collection = Get(instance);
				foreach (var element in value.AsValid<IEnumerable>())
				{
					base.Assign(collection, element);
				}
			}
		}


		sealed class Specification : ISpecification<MemberInformation>
		{
			public static Specification Instance { get; } = new Specification();
			Specification() : this(IsCollectionTypeSpecification.Default) {}
			readonly ISpecification<TypeInfo> _specification;


			Specification(ISpecification<TypeInfo> specification)
			{
				_specification = specification;
			}

			public bool IsSatisfiedBy(MemberInformation parameter) => _specification.IsSatisfiedBy(parameter.MemberType);
		}
	}


	class MemberContextSelector : OptionSelector<MemberInformation, IMemberContext>, IMemberContextSelector
	{
		public MemberContextSelector(IContexts contexts, IAddDelegates add)
			: this(new MemberOption(contexts), new ReadOnlyCollectionMemberOption(contexts, add)) {}

		public MemberContextSelector(params IOption<MemberInformation, IMemberContext>[] options) : base(options) {}
	}

	public interface IMemberOption : IOption<MemberInformation, IMemberContext> {}

	public abstract class MemberOptionBase : OptionBase<MemberInformation, IMemberContext>, IMemberOption
	{
		readonly IParameterizedSource<MemberInformation, IMemberName> _provider;

		protected MemberOptionBase(ISpecification<MemberInformation> specification)
			: this(specification, MemberNameProvider.Default) {}

		protected MemberOptionBase(ISpecification<MemberInformation> specification,
		                           IParameterizedSource<MemberInformation, IMemberName> provider)
			: base(specification)
		{
			_provider = provider;
		}

		public override IMemberContext Get(MemberInformation parameter) => Create(_provider.Get(parameter));

		protected abstract IMemberContext Create(IMemberName name);
	}

	public class MemberNameProvider : IParameterizedSource<MemberInformation, IMemberName>
	{
		readonly IAliasProvider _alias;
		public static MemberNameProvider Default { get; } = new MemberNameProvider();
		MemberNameProvider() : this(MemberAliasProvider.Default) {}

		public MemberNameProvider(IAliasProvider alias)
		{
			_alias = alias;
		}

		public IMemberName Get(MemberInformation parameter)
			=>
				new MemberName(_alias.Get(parameter.Metadata) ?? parameter.Metadata.Name, parameter.Metadata, parameter.MemberType);
	}

	public interface IMemberName : IName
	{
		MemberInfo Metadata { get; }

		TypeInfo MemberType { get; }
	}

	/*public interface ICollectionName : IName
	{
		IName ElementName { get; }
	}

	class CollectionName : Name, ICollectionName
	{
		public CollectionName(string displayName, TypeInfo classification, IName elementName)
			: base(displayName, classification)
		{
			ElementName = elementName;
		}

		public IName ElementName { get; }
	}*/

	class MemberName : Name, IMemberName
	{
		public MemberName(string displayName, MemberInfo metadata, TypeInfo memberType)
			: base(displayName, metadata.DeclaringType)
		{
			Metadata = metadata;
			MemberType = memberType;
		}

		public MemberInfo Metadata { get; }
		public TypeInfo MemberType { get; }
	}

	public class MemberOption : MemberOptionBase
	{
		readonly IGetterFactory _getter;
		readonly ISetterFactory _setter;
		readonly IContexts _contexts;

		public MemberOption(IContexts contexts) : this(contexts, GetterFactory.Default, SetterFactory.Default) {}

		public MemberOption(IContexts contexts, IGetterFactory getter, ISetterFactory setter)
			: base(new DelegatedSpecification<MemberInformation>(x => x.Assignable))
		{
			_getter = getter;
			_setter = setter;
			_contexts = contexts;
		}

		protected override IMemberContext Create(IMemberName name)
		{
			var metadata = name.Metadata;
			var getter = _getter.Get(metadata);
			var setter = _setter.Get(metadata);
			var result = new MemberContext(name, _contexts.Get(name.MemberType), setter, getter);
			return result;
		}
	}

	/*public abstract class MemberContextBase : NamedContext, IMemberContext
	{
		protected MemberContextBase(IName name, IElementContext body) : this(name.DisplayName, name, body) {}

		protected MemberContextBase(string displayName, IName name, IElementContext body) : base(name, body)
		{
			DisplayName = displayName;
		}

		/*public abstract object Get(object instance);
		public abstract void Assign(object instance, object value);#1#
		public string DisplayName { get; }

		public override void Emit(IEmitter emitter, object instance)
		{
			base.Emit(emitter, instance);
		}
	}*/


	public class MemberContext : NamedContext<IMemberName>, IMemberContext
	{
		readonly Action<object, object> _setter;
		readonly Func<object, object> _getter;

		/*public MemberContext(MemberInfo metadata, Action<object, object> setter, Func<object, object> getter,
		                     IElementContext context)
			: this(metadata.Name, metadata, setter, getter, context) {}*/

		public MemberContext(IMemberName name, IElementContext body, Action<object, object> setter,
		                     Func<object, object> getter) : this(name.DisplayName, name, body, setter, getter) {}

		public MemberContext(string displayName, IMemberName name, IElementContext body, Action<object, object> setter,
		                     Func<object, object> getter) : base(name, body)
		{
			DisplayName = displayName;
			_setter = setter;
			_getter = getter;
		}

		public override void Emit(IEmitter emitter, object instance)
		{
			var value = Get(instance);
			if (value != null)
			{
				base.Emit(emitter, value);
			}
		}

		public string DisplayName { get; }

		protected virtual object Get(object instance) => _getter(instance);

		protected virtual void Assign(object instance, object value)
		{
			if (value != null)
			{
				_setter(instance, value);
			}
		}
	}


	public interface IMemberContextSelector : ISelector<MemberInformation, IMemberContext> {}

	public sealed class ContextMembers : CacheBase<TypeInfo, IMembers>, IContextMembers
	{
		readonly IMemberContextSelector _selector;
		readonly ISpecification<PropertyInfo> _property;
		readonly ISpecification<FieldInfo> _field;

		public ContextMembers(IMemberContextSelector selector, ISpecification<PropertyInfo> property,
		                      ISpecification<FieldInfo> field)
		{
			_selector = selector;
			_property = property;
			_field = field;
		}

		protected override IMembers Create(TypeInfo parameter) =>
			new Members(Yield(parameter).OrderBy(x => x.Sort).Select(x => x.Member));

		IEnumerable<Sorting> Yield(TypeInfo parameter)
		{
			foreach (var property in parameter.GetProperties())
			{
				if (_property.IsSatisfiedBy(property))
				{
					var sorting = Create(property, property.PropertyType, property.CanWrite);
					if (sorting != null)
					{
						yield return sorting.Value;
					}
				}
			}

			foreach (var field in parameter.GetFields())
			{
				if (_field.IsSatisfiedBy(field))
				{
					var sorting = Create(field, field.FieldType, !field.IsInitOnly);
					if (sorting != null)
					{
						yield return sorting.Value;
					}
				}
			}
		}

		Sorting? Create(MemberInfo metadata, Type memberType, bool assignable)
		{
			var sort = new Sort(metadata.GetCustomAttribute<XmlElementAttribute>(false)?.Order, metadata.MetadataToken);
			var information = new MemberInformation(metadata, memberType.GetTypeInfo().AccountForNullable(), assignable);
			var element = _selector.Get(information);
			var result = element != null ? (Sorting?) new Sorting(element, sort) : null;
			return result;
		}

		struct Sorting
		{
			public Sorting(IMemberContext member, Sort sort)
			{
				Member = member;
				Sort = sort;
			}

			public IMemberContext Member { get; }
			public Sort Sort { get; }
		}
	}

	public abstract class ContextBase : IElementContext
	{
		protected ContextBase(TypeInfo classification)
		{
			Classification = classification;
		}

		public abstract void Emit(IEmitter emitter, object instance);
		public TypeInfo Classification { get; }
		public abstract object Yield(IYielder yielder);
	}

	public abstract class ContextBase<T> : ContextBase
	{
		protected ContextBase(TypeInfo classification) : base(classification) {}

		public override void Emit(IEmitter emitter, object instance) => Emit(emitter, (T) instance);

		public abstract void Emit(IEmitter emitter, T instance);
	}

	public class NamedContext : NamedContext<IName>
	{
		public NamedContext(IName name, IElementContext body) : base(name, body) {}
	}

	public class NamedContext<T> : DecoratedContext where T : IName
	{
		public NamedContext(T name, IElementContext body) : this(name, body, body.Classification) {}

		public NamedContext(T name, IElementContext body, TypeInfo classification) : base(body, classification)
		{
			Name = name;
		}

		protected T Name { get; }

		public override void Emit(IEmitter emitter, object instance)
		{
			using (emitter.Emit(Name))
			{
				base.Emit(emitter, instance);
			}
		}
	}

	public class DecoratedContext : ContextBase
	{
		readonly IElementContext _context;

		/*public DecoratedContext(IElementContext context) : this(context, context.Classification) {}*/

		public DecoratedContext(IElementContext context, TypeInfo classification) : base(classification)
		{
			_context = context;
		}

		public override void Emit(IEmitter emitter, object instance) => _context.Emit(emitter, instance);
		public override object Yield(IYielder yielder) => _context.Yield(yielder);
	}
}