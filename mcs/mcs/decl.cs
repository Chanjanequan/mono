//
// decl.cs: Declaration base class for structs, classes, enums and interfaces.
//
// Author: Miguel de Icaza (miguel@gnu.org)
//         Marek Safar (marek.safar@seznam.cz)
//
// Licensed under the terms of the GNU GPL
//
// (C) 2001 Ximian, Inc (http://www.ximian.com)
// (C) 2004 Novell, Inc
//
// TODO: Move the method verification stuff from the class.cs and interface.cs here
//

using System;
using System.Collections;
using System.Globalization;
using System.Reflection.Emit;
using System.Reflection;

#if BOOTSTRAP_WITH_OLDLIB
using XmlElement = System.Object;
#else
using System.Xml;
#endif

namespace Mono.CSharp {

	public class MemberName {
		public readonly string Name;
		public readonly MemberName Left;
		public readonly Location Location;

		bool is_double_colon;

		public static readonly MemberName Null = new MemberName ("");

		private MemberName (MemberName left, string name, bool is_double_colon, Location loc)
		{
			this.Name = name;
			this.Location = loc;
			this.is_double_colon = is_double_colon;
			this.Left = left;
		}

		public MemberName (string name)
			: this (null, name, false, Location.Null)
		{
		}

		public MemberName (MemberName left, string name)
			: this (left, name, false, left != null ? left.Location : Location.Null)
		{
		}

		public MemberName (string name, Location loc)
			: this (null, name, false, loc)
		{
		}

		public MemberName (MemberName left, string name, Location loc)
			: this (left, name, false, loc)
		{
		}

		public MemberName (string alias, string name, Location loc)
			: this (new MemberName (alias), name, true, loc)
		{
		}

		public MemberName (MemberName left, MemberName right)
			: this (left, right, right.Location)
		{
		}

		public MemberName (MemberName left, MemberName right, Location loc)
			: this (null, right.Name, false, loc)
		{
			if (right.is_double_colon)
				throw new InternalErrorException ("Cannot append double_colon member name");
			this.Left = (right.Left == null) ? left : new MemberName (left, right.Left);
		}

		static readonly char [] dot_array = { '.' };

		public static MemberName FromDotted (string name)
		{
			string [] elements = name.Split (dot_array);
			int count = elements.Length;
			int i = 0;
			MemberName n = new MemberName (elements [i++]);
			while (i < count)
				n = new MemberName (n, elements [i++]);
			return n;
		}

		public string GetName ()
		{
			return GetName (false);
		}

		public string GetName (bool is_generic)
		{
			string name = is_generic ? Basename : Name;
			string connect = is_double_colon ? "::" : ".";
			if (Left != null)
				return Left.GetName (is_generic) + connect + name;
			else
				return name;
		}

		///
		/// This returns exclusively the name as seen on the source code
		/// it is not the fully qualifed type after resolution
		///
		public string GetPartialName ()
		{
			string connect = is_double_colon ? "::" : ".";
			if (Left != null)
				return Left.GetPartialName () + connect + Name;
			else
				return Name;
		}

		public string GetTypeName ()
		{
			string connect = is_double_colon ? "::" : ".";
			if (Left != null)
				return Left.GetTypeName () + connect + Name;
			else
				return Name;
		}

		public Expression GetTypeExpression ()
		{
			if (Left == null)
				return new SimpleName (Name, Location);
			if (is_double_colon) {
				if (Left.Left != null)
					throw new InternalErrorException ("The left side of a :: should be an identifier");
				return new QualifiedAliasMember (Left.Name, Name, Location);
			}

			Expression lexpr = Left.GetTypeExpression ();
			return new MemberAccess (lexpr, Name);
		}

		public MemberName Clone ()
		{
			MemberName left_clone = Left == null ? null : Left.Clone ();
			return new MemberName (left_clone, Name, is_double_colon, Location);
		}

		public string Basename {
			get { return Name; }
		}

		public override string ToString ()
		{
			string connect = is_double_colon ? "::" : ".";
			if (Left != null)
				return Left + connect + Name;
			else
				return Name;
		}

		public override bool Equals (object other)
		{
			return Equals (other as MemberName);
		}

		public bool Equals (MemberName other)
		{
			if (this == other)
				return true;
			if (other == null || Name != other.Name)
				return false;
			if (is_double_colon != other.is_double_colon)
				return false;
#if NET_2_0
			if (TypeArguments == null)
				return other.TypeArguments == null;

			if (other.TypeArguments == null || TypeArguments.Count != other.TypeArguments.Count)
				return false;
#endif
			if (Left == null)
				return other.Left == null;

			return Left.Equals (other.Left);
		}

		public override int GetHashCode ()
		{
			int hash = Name.GetHashCode ();
			for (MemberName n = Left; n != null; n = n.Left)
				hash ^= n.Name.GetHashCode ();
			if (is_double_colon)
				hash ^= 0xbadc01d;
#if NET_2_0
			if (TypeArguments != null)
				hash ^= TypeArguments.Count << 5;
#endif

			return hash & 0x7FFFFFFF;
		}
	}

	/// <summary>
	///   Base representation for members.  This is used to keep track
	///   of Name, Location and Modifier flags, and handling Attributes.
	/// </summary>
	public abstract class MemberCore : Attributable, IResolveContext {
		/// <summary>
		///   Public name
		/// </summary>

		protected string cached_name;
		public string Name {
			get {
				if (cached_name == null)
					cached_name = MemberName.GetName (false);
				return cached_name;
			}
		}

                // Is not readonly because of IndexerName attribute
		private MemberName member_name;
		public MemberName MemberName {
			get { return member_name; }
		}

		/// <summary>
		///   Modifier flags that the user specified in the source code
		/// </summary>
		public int ModFlags;

		public readonly DeclSpace Parent;

		/// <summary>
		///   Location where this declaration happens
		/// </summary>
		public Location Location {
			get { return member_name.Location; }
		}

		/// <summary>
		///   XML documentation comment
		/// </summary>
		protected string comment;

		/// <summary>
		///   Represents header string for documentation comment 
		///   for each member types.
		/// </summary>
		public abstract string DocCommentHeader { get; }

		[Flags]
		public enum Flags {
			Obsolete_Undetected = 1,		// Obsolete attribute has not been detected yet
			Obsolete = 1 << 1,			// Type has obsolete attribute
			ClsCompliance_Undetected = 1 << 2,	// CLS Compliance has not been detected yet
			ClsCompliant = 1 << 3,			// Type is CLS Compliant
			CloseTypeCreated = 1 << 4,		// Tracks whether we have Closed the type
			HasCompliantAttribute_Undetected = 1 << 5,	// Presence of CLSCompliantAttribute has not been detected
			HasClsCompliantAttribute = 1 << 6,			// Type has CLSCompliantAttribute
			ClsCompliantAttributeTrue = 1 << 7,			// Type has CLSCompliant (true)
			Excluded_Undetected = 1 << 8,		// Conditional attribute has not been detected yet
			Excluded = 1 << 9,					// Method is conditional
			TestMethodDuplication = 1 << 10,		// Test for duplication must be performed
			IsUsed = 1 << 11,
			IsAssigned = 1 << 12,				// Field is assigned
			HasExplicitLayout	= 1 << 13
		}

		/// <summary>
		///   MemberCore flags at first detected then cached
		/// </summary>
		internal Flags caching_flags;

		public MemberCore (DeclSpace parent, MemberName name, Attributes attrs)
			: base (attrs)
		{
			this.Parent = parent;
			member_name = name;
			caching_flags = Flags.Obsolete_Undetected | Flags.ClsCompliance_Undetected | Flags.HasCompliantAttribute_Undetected | Flags.Excluded_Undetected;
		}

		protected virtual void SetMemberName (MemberName new_name)
		{
			member_name = new_name;
			cached_name = null;
		}

		public abstract bool Define ();

		public virtual string DocComment {
			get {
				return comment;
			}
			set {
				comment = value;
			}
		}

		// 
		// Returns full member name for error message
		//
		public virtual string GetSignatureForError ()
		{
			if (Parent == null || Parent.Parent == null)
				return Name;

			return String.Concat (Parent.GetSignatureForError (), '.', Name);
		}

		/// <summary>
		/// Base Emit method. This is also entry point for CLS-Compliant verification.
		/// </summary>
		public virtual void Emit ()
		{
			if (!RootContext.VerifyClsCompliance)
				return;

			VerifyClsCompliance ();
		}

		public virtual bool IsUsed {
			get { return (caching_flags & Flags.IsUsed) != 0; }
		}

		public void SetMemberIsUsed ()
		{
			caching_flags |= Flags.IsUsed;
		}

		/// <summary>
		/// Returns instance of ObsoleteAttribute for this MemberCore
		/// </summary>
		public virtual ObsoleteAttribute GetObsoleteAttribute ()
		{
			// ((flags & (Flags.Obsolete_Undetected | Flags.Obsolete)) == 0) is slower, but why ?
			if ((caching_flags & Flags.Obsolete_Undetected) == 0 && (caching_flags & Flags.Obsolete) == 0) {
				return null;
			}

			caching_flags &= ~Flags.Obsolete_Undetected;

			if (OptAttributes == null)
				return null;

			Attribute obsolete_attr = OptAttributes.Search (
				TypeManager.obsolete_attribute_type);
			if (obsolete_attr == null)
				return null;

			ObsoleteAttribute obsolete = obsolete_attr.GetObsoleteAttribute ();
			if (obsolete == null)
				return null;

			caching_flags |= Flags.Obsolete;
			return obsolete;
		}

		/// <summary>
		/// Checks for ObsoleteAttribute presence. It's used for testing of all non-types elements
		/// </summary>
		public virtual void CheckObsoleteness (Location loc)
		{
			if (Parent != null)
				Parent.CheckObsoleteness (loc);

			ObsoleteAttribute oa = GetObsoleteAttribute ();
			if (oa == null) {
				return;
			}

			AttributeTester.Report_ObsoleteMessage (oa, GetSignatureForError (), loc);
		}

		/// <summary>
		/// Analyze whether CLS-Compliant verification must be execute for this MemberCore.
		/// </summary>
		public override bool IsClsComplianceRequired ()
		{
			if ((caching_flags & Flags.ClsCompliance_Undetected) == 0)
				return (caching_flags & Flags.ClsCompliant) != 0;

			if (GetClsCompliantAttributeValue () && IsExposedFromAssembly ()) {
				caching_flags &= ~Flags.ClsCompliance_Undetected;
				caching_flags |= Flags.ClsCompliant;
				return true;
			}

			caching_flags &= ~Flags.ClsCompliance_Undetected;
			return false;
		}

		/// <summary>
		/// Returns true when MemberCore is exposed from assembly.
		/// </summary>
		public bool IsExposedFromAssembly ()
		{
			if ((ModFlags & (Modifiers.PUBLIC | Modifiers.PROTECTED)) == 0)
				return false;
			
			DeclSpace parentContainer = Parent;
			while (parentContainer != null && parentContainer.ModFlags != 0) {
				if ((parentContainer.ModFlags & (Modifiers.PUBLIC | Modifiers.PROTECTED)) == 0)
					return false;
				parentContainer = parentContainer.Parent;
			}
			return true;
		}

		/// <summary>
		/// Goes through class hierarchy and gets value of first found CLSCompliantAttribute.
		/// If no is attribute exists then assembly CLSCompliantAttribute is returned.
		/// </summary>
		public virtual bool GetClsCompliantAttributeValue ()
		{
			if ((caching_flags & Flags.HasCompliantAttribute_Undetected) == 0)
				return (caching_flags & Flags.ClsCompliantAttributeTrue) != 0;

			caching_flags &= ~Flags.HasCompliantAttribute_Undetected;

			if (OptAttributes != null) {
				Attribute cls_attribute = OptAttributes.Search (
					TypeManager.cls_compliant_attribute_type);
				if (cls_attribute != null) {
					caching_flags |= Flags.HasClsCompliantAttribute;
					return cls_attribute.GetClsCompliantAttributeValue ();
				}
			}

			if (Parent.GetClsCompliantAttributeValue ()) {
				caching_flags |= Flags.ClsCompliantAttributeTrue;
				return true;
			}
			return false;
		}

		/// <summary>
		/// Returns true if MemberCore is explicitly marked with CLSCompliantAttribute
		/// </summary>
		protected bool HasClsCompliantAttribute {
			get {
				return (caching_flags & Flags.HasClsCompliantAttribute) != 0;
			}
		}

		/// <summary>
		/// It helps to handle error 102 & 111 detection
		/// </summary>
		public virtual bool MarkForDuplicationCheck ()
		{
			return false;
		}

		/// <summary>
		/// The main virtual method for CLS-Compliant verifications.
		/// The method returns true if member is CLS-Compliant and false if member is not
		/// CLS-Compliant which means that CLS-Compliant tests are not necessary. A descendants override it
		/// and add their extra verifications.
		/// </summary>
		protected virtual bool VerifyClsCompliance ()
		{
			if (!IsClsComplianceRequired ()) {
				if (HasClsCompliantAttribute && RootContext.WarningLevel >= 2) {
					if (!IsExposedFromAssembly ())
						Report.Warning (3019, 2, Location, "CLS compliance checking will not be performed on `{0}' because it is not visible from outside this assembly", GetSignatureForError ());
					if (!CodeGen.Assembly.IsClsCompliant)
						Report.Warning (3021, 2, Location, "`{0}' does not need a CLSCompliant attribute because the assembly is not marked as CLS-compliant", GetSignatureForError ());
				}
				return false;
			}

			if (!CodeGen.Assembly.IsClsCompliant) {
				if (HasClsCompliantAttribute) {
					Report.Error (3014, Location,
						"`{0}' cannot be marked as CLS-compliant because the assembly is not marked as CLS-compliant",
						GetSignatureForError ());
				}
				return false;
			}

			if (member_name.Name [0] == '_') {
				Report.Error (3008, Location, "Identifier `{0}' is not CLS-compliant", GetSignatureForError () );
			}
			return true;
		}

		//
		// Raised (and passed an XmlElement that contains the comment)
		// when GenerateDocComment is writing documentation expectedly.
		//
		internal virtual void OnGenerateDocComment (XmlElement intermediateNode)
		{
		}

		//
		// Returns a string that represents the signature for this 
		// member which should be used in XML documentation.
		//
		public virtual string GetDocCommentName (DeclSpace ds)
		{
			if (ds == null || this is DeclSpace)
				return DocCommentHeader + Name;
			else
				return String.Concat (DocCommentHeader, ds.Name, ".", Name);
		}

		//
		// Generates xml doc comments (if any), and if required,
		// handle warning report.
		//
		internal virtual void GenerateDocComment (DeclSpace ds)
		{
			DocUtil.GenerateDocComment (this, ds);
		}

		public override IResolveContext ResolveContext {
			get {
				return this;
			}
		}

		#region IResolveContext Members

		public virtual DeclSpace DeclContainer {
			get {
				return Parent;
			}
		}

		public bool IsInObsoleteScope {
			get {
				if (GetObsoleteAttribute () != null)
					return true;

				return Parent == null ? false : Parent.IsInObsoleteScope;
			}
		}

		public bool IsInUnsafeScope {
			get {
				if ((ModFlags & Modifiers.UNSAFE) != 0)
					return true;

				return Parent == null ? false : Parent.IsInUnsafeScope;
			}
		}

		#endregion
	}

	/// <summary>
	///   Base class for structs, classes, enumerations and interfaces.  
	/// </summary>
	/// <remarks>
	///   They all create new declaration spaces.  This
	///   provides the common foundation for managing those name
	///   spaces.
	/// </remarks>
	public abstract class DeclSpace : MemberCore {

		/// <summary>
		///   This points to the actual definition that is being
		///   created with System.Reflection.Emit
		/// </summary>
		public TypeBuilder TypeBuilder;

		//
		// This is the namespace in which this typecontainer
		// was declared.  We use this to resolve names.
		//
		public NamespaceEntry NamespaceEntry;

		private Hashtable Cache = new Hashtable ();
		
		public string Basename;
		
		protected Hashtable defined_names;

		static string[] attribute_targets = new string [] { "type" };

		public DeclSpace (NamespaceEntry ns, DeclSpace parent, MemberName name,
				  Attributes attrs)
			: base (parent, name, attrs)
		{
			NamespaceEntry = ns;
			Basename = name.Name;
			defined_names = new Hashtable ();
		}

		public override DeclSpace DeclContainer {
			get {
				return this;
			}
		}

		/// <summary>
		/// Adds the member to defined_names table. It tests for duplications and enclosing name conflicts
		/// </summary>
		protected virtual bool AddToContainer (MemberCore symbol, string name)
		{
			MemberCore mc = (MemberCore) defined_names [name];

			if (mc == null) {
				defined_names.Add (name, symbol);
				return true;
			}

			if (symbol.MarkForDuplicationCheck () && mc.MarkForDuplicationCheck ())
				return true;

			Report.SymbolRelatedToPreviousError (mc);
			if ((mc.ModFlags & Modifiers.PARTIAL) != 0 && (symbol is ClassOrStruct || symbol is Interface)) {
				Error_MissingPartialModifier (symbol);
				return false;
			}

			if (this is RootTypes) {
				Report.Error (101, symbol.Location, 
					"The namespace `{0}' already contains a definition for `{1}'",
					((DeclSpace)symbol).NamespaceEntry.GetSignatureForError (), symbol.MemberName.Name);
			} else {
				Report.Error (102, symbol.Location, "The type `{0}' already contains a definition for `{1}'",
					GetSignatureForError (), symbol.MemberName.Name);
			}
			return false;
		}

		/// <summary>
		///   Returns the MemberCore associated with a given name in the declaration
		///   space. It doesn't return method based symbols !!
		/// </summary>
		/// 
		public MemberCore GetDefinition (string name)
		{
			return (MemberCore)defined_names [name];
		}
		
		// 
		// root_types contains all the types.  All TopLevel types
		// hence have a parent that points to `root_types', that is
		// why there is a non-obvious test down here.
		//
		public bool IsTopLevel {
			get { return (Parent != null && Parent.Parent == null); }
		}

		public virtual void CloseType ()
		{
			if ((caching_flags & Flags.CloseTypeCreated) == 0){
				try {
					TypeBuilder.CreateType ();
				} catch {
					//
					// The try/catch is needed because
					// nested enumerations fail to load when they
					// are defined.
					//
					// Even if this is the right order (enumerations
					// declared after types).
					//
					// Note that this still creates the type and
					// it is possible to save it
				}
				caching_flags |= Flags.CloseTypeCreated;
			}
		}

		protected virtual TypeAttributes TypeAttr {
			get { return CodeGen.Module.DefaultCharSetType; }
		}

		/// <remarks>
		///  Should be overriten by the appropriate declaration space
		/// </remarks>
		public abstract TypeBuilder DefineType ();
		
		/// <summary>
		///   Define all members, but don't apply any attributes or do anything which may
		///   access not-yet-defined classes.  This method also creates the MemberCache.
		/// </summary>
		public virtual bool DefineMembers ()
		{
			if (((ModFlags & Modifiers.NEW) != 0) && IsTopLevel) {
				Report.Error (1530, Location, "Keyword `new' is not allowed on namespace elements");
				return false;
			}
			return true;
		}

		protected void Error_MissingPartialModifier (MemberCore type)
		{
			Report.Error (260, type.Location,
				"Missing partial modifier on declaration of type `{0}'. Another partial declaration of this type exists",
				type.GetSignatureForError ());
		}

		public override string GetSignatureForError ()
		{	
			// Parent.GetSignatureForError
			return Name;
		}
		
		public bool CheckAccessLevel (Type check_type)
		{
			if (check_type == TypeBuilder)
				return true;
			
			TypeAttributes check_attr = check_type.Attributes & TypeAttributes.VisibilityMask;

			//
			// Broken Microsoft runtime, return public for arrays, no matter what 
			// the accessibility is for their underlying class, and they return 
			// NonPublic visibility for pointers
			//
			if (check_type.IsArray || check_type.IsPointer)
				return CheckAccessLevel (TypeManager.GetElementType (check_type));

			if (TypeBuilder == null)
				// FIXME: TypeBuilder will be null when invoked by Class.GetNormalBases().
				//        However, this is invoked again later -- so safe to return true.
				//        May also be null when resolving top-level attributes.
				return true;

			switch (check_attr){
			case TypeAttributes.Public:
				return true;

			case TypeAttributes.NotPublic:
				//
				// This test should probably use the declaringtype.
				//
				return check_type.Assembly == TypeBuilder.Assembly;
				
			case TypeAttributes.NestedPublic:
				return true;

			case TypeAttributes.NestedPrivate:
				return NestedAccessible (check_type);

			case TypeAttributes.NestedFamily:
				return FamilyAccessible (check_type);

			case TypeAttributes.NestedFamANDAssem:
				return (check_type.Assembly == TypeBuilder.Assembly) &&
					FamilyAccessible (check_type);

			case TypeAttributes.NestedFamORAssem:
				return (check_type.Assembly == TypeBuilder.Assembly) ||
					FamilyAccessible (check_type);

			case TypeAttributes.NestedAssembly:
				return check_type.Assembly == TypeBuilder.Assembly;
			}

			Console.WriteLine ("HERE: " + check_attr);
			return false;

		}

		protected bool NestedAccessible (Type check_type)
		{
			Type declaring = check_type.DeclaringType;
			return TypeBuilder == declaring ||
				TypeManager.IsNestedChildOf (TypeBuilder, declaring);
		}

		protected bool FamilyAccessible (Type check_type)
		{
			Type declaring = check_type.DeclaringType;
			return TypeManager.IsNestedFamilyAccessible (TypeBuilder, declaring);
		}

		// Access level of a type.
		const int X = 1;
		enum AccessLevel { // Each column represents `is this scope larger or equal to Blah scope'
		                            // Public    Assembly   Protected
			Protected           = (0 << 0) | (0 << 1) | (X << 2),
			Public              = (X << 0) | (X << 1) | (X << 2),
			Private             = (0 << 0) | (0 << 1) | (0 << 2),
			Internal            = (0 << 0) | (X << 1) | (0 << 2),
			ProtectedOrInternal = (0 << 0) | (X << 1) | (X << 2),
		}
		
		static AccessLevel GetAccessLevelFromModifiers (int flags)
		{
			if ((flags & Modifiers.INTERNAL) != 0) {
				
				if ((flags & Modifiers.PROTECTED) != 0)
					return AccessLevel.ProtectedOrInternal;
				else
					return AccessLevel.Internal;
				
			} else if ((flags & Modifiers.PROTECTED) != 0)
				return AccessLevel.Protected;
			
			else if ((flags & Modifiers.PRIVATE) != 0)
				return AccessLevel.Private;
			
			else
				return AccessLevel.Public;
		}

		// What is the effective access level of this?
		// TODO: Cache this?
		AccessLevel EffectiveAccessLevel {
			get {
				AccessLevel myAccess = GetAccessLevelFromModifiers (ModFlags);
				if (!IsTopLevel && (Parent != null))
					return myAccess & Parent.EffectiveAccessLevel;
				else
					return myAccess;
			}
		}

		// Return the access level for type `t'
		static AccessLevel TypeEffectiveAccessLevel (Type t)
		{
			if (t.IsPublic)
				return AccessLevel.Public;		
			if (t.IsNestedPrivate)
				return AccessLevel.Private;
			if (t.IsNotPublic)
				return AccessLevel.Internal;
			
			// By now, it must be nested
			AccessLevel parentLevel = TypeEffectiveAccessLevel (t.DeclaringType);
			
			if (t.IsNestedPublic)
				return parentLevel;
			if (t.IsNestedAssembly)
				return parentLevel & AccessLevel.Internal;
			if (t.IsNestedFamily)
				return parentLevel & AccessLevel.Protected;
			if (t.IsNestedFamORAssem)
				return parentLevel & AccessLevel.ProtectedOrInternal;
			if (t.IsNestedFamANDAssem)
				throw new NotImplementedException ("NestedFamANDAssem not implemented, cant make this kind of type from c# anyways");
			
			// nested private is taken care of
			
			throw new Exception ("I give up, what are you?");
		}

		//
		// This answers `is the type P, as accessible as a member M which has the
		// accessability @flags which is declared as a nested member of the type T, this declspace'
		//
		public bool AsAccessible (Type p, int flags)
		{
			//
			// 1) if M is private, its accessability is the same as this declspace.
			// we already know that P is accessible to T before this method, so we
			// may return true.
			//
			
			if ((flags & Modifiers.PRIVATE) != 0)
				return true;
			
			while (p.IsArray || p.IsPointer || p.IsByRef)
				p = TypeManager.GetElementType (p);
			
			AccessLevel pAccess = TypeEffectiveAccessLevel (p);
			AccessLevel mAccess = this.EffectiveAccessLevel &
				GetAccessLevelFromModifiers (flags);
			
			// for every place from which we can access M, we must
			// be able to access P as well. So, we want
			// For every bit in M and P, M_i -> P_1 == true
			// or, ~ (M -> P) == 0 <-> ~ ( ~M | P) == 0
			
			return ~ (~ mAccess | pAccess) == 0;
		}

		//
		// Return the nested type with name @name.  Ensures that the nested type
		// is defined if necessary.  Do _not_ use this when you have a MemberCache handy.
		//
		public virtual Type FindNestedType (string name)
		{
			return null;
		}

		private Type LookupNestedTypeInHierarchy (string name)
		{
			// if the member cache has been created, lets use it.
			// the member cache is MUCH faster.
			if (MemberCache != null)
				return MemberCache.FindNestedType (name);

			// no member cache. Do it the hard way -- reflection
			Type t = null;
			for (Type current_type = TypeBuilder;
			     current_type != null && current_type != TypeManager.object_type;
			     current_type = current_type.BaseType) {
				if (current_type is TypeBuilder) {
					DeclSpace decl = this;
					if (current_type != TypeBuilder)
						decl = TypeManager.LookupDeclSpace (current_type);
					t = decl.FindNestedType (name);
				} else {
					t = TypeManager.GetNestedType (current_type, name);
				}

				if (t != null && CheckAccessLevel (t))
					return t;
			}

			return null;
		}

		//
		// Public function used to locate types.
		//
		// Set 'ignore_cs0104' to true if you want to ignore cs0104 errors.
		//
		// Returns: Type or null if they type can not be found.
		//
		public FullNamedExpression LookupType (string name, Location loc, bool ignore_cs0104)
		{
			if (Cache.Contains (name))
				return (FullNamedExpression) Cache [name];

			FullNamedExpression e;
			Type t = LookupNestedTypeInHierarchy (name);
			if (t != null)
				e = new TypeExpression (t, Location.Null);
			else if (Parent != null && Parent != RootContext.Tree.Types)
				e = Parent.LookupType (name, loc, ignore_cs0104);
			else
				e = NamespaceEntry.LookupNamespaceOrType (this, name, loc, ignore_cs0104);

			Cache [name] = e;
			return e;
		}

		/// <remarks>
		///   This function is broken and not what you're looking for.  It should only
		///   be used while the type is still being created since it doesn't use the cache
		///   and relies on the filter doing the member name check.
		/// </remarks>
		public abstract MemberList FindMembers (MemberTypes mt, BindingFlags bf,
							MemberFilter filter, object criteria);

		/// <remarks>
		///   If we have a MemberCache, return it.  This property may return null if the
		///   class doesn't have a member cache or while it's still being created.
		/// </remarks>
		public abstract MemberCache MemberCache {
			get;
		}

		public override void ApplyAttributeBuilder (Attribute a, CustomAttributeBuilder cb)
		{
			if (a.Type == TypeManager.required_attr_type) {
				Report.Error (1608, a.Location, "The RequiredAttribute attribute is not permitted on C# types");
				return;
			}
			TypeBuilder.SetCustomAttribute (cb);
		}

		public override string[] ValidAttributeTargets {
			get { return attribute_targets; }
		}

		protected override bool VerifyClsCompliance ()
		{
			if (!base.VerifyClsCompliance ()) {
				return false;
			}

			IDictionary cache = TypeManager.AllClsTopLevelTypes;
			string lcase = Name.ToLower (System.Globalization.CultureInfo.InvariantCulture);
			if (!cache.Contains (lcase)) {
				cache.Add (lcase, this);
				return true;
			}

			object val = cache [lcase];
			if (val == null) {
				Type t = AttributeTester.GetImportedIgnoreCaseClsType (lcase);
				if (t == null)
					return true;
				Report.SymbolRelatedToPreviousError (t);
			}
			else {
				Report.SymbolRelatedToPreviousError ((DeclSpace)val);
			}
			Report.Error (3005, Location, "Identifier `{0}' differing only in case is not CLS-compliant", GetSignatureForError ());
			return true;
		}
	}

	/// <summary>
	///   This is a readonly list of MemberInfo's.      
	/// </summary>
	public class MemberList : IList {
		public readonly IList List;
		int count;

		/// <summary>
		///   Create a new MemberList from the given IList.
		/// </summary>
		public MemberList (IList list)
		{
			if (list != null)
				this.List = list;
			else
				this.List = new ArrayList ();
			count = List.Count;
		}

		/// <summary>
		///   Concatenate the ILists `first' and `second' to a new MemberList.
		/// </summary>
		public MemberList (IList first, IList second)
		{
			ArrayList list = new ArrayList ();
			list.AddRange (first);
			list.AddRange (second);
			count = list.Count;
			List = list;
		}

		public static readonly MemberList Empty = new MemberList (new ArrayList ());

		/// <summary>
		///   Cast the MemberList into a MemberInfo[] array.
		/// </summary>
		/// <remarks>
		///   This is an expensive operation, only use it if it's really necessary.
		/// </remarks>
		public static explicit operator MemberInfo [] (MemberList list)
		{
			Timer.StartTimer (TimerType.MiscTimer);
			MemberInfo [] result = new MemberInfo [list.Count];
			list.CopyTo (result, 0);
			Timer.StopTimer (TimerType.MiscTimer);
			return result;
		}

		// ICollection

		public int Count {
			get {
				return count;
			}
		}

		public bool IsSynchronized {
			get {
				return List.IsSynchronized;
			}
		}

		public object SyncRoot {
			get {
				return List.SyncRoot;
			}
		}

		public void CopyTo (Array array, int index)
		{
			List.CopyTo (array, index);
		}

		// IEnumerable

		public IEnumerator GetEnumerator ()
		{
			return List.GetEnumerator ();
		}

		// IList

		public bool IsFixedSize {
			get {
				return true;
			}
		}

		public bool IsReadOnly {
			get {
				return true;
			}
		}

		object IList.this [int index] {
			get {
				return List [index];
			}

			set {
				throw new NotSupportedException ();
			}
		}

		// FIXME: try to find out whether we can avoid the cast in this indexer.
		public MemberInfo this [int index] {
			get {
				return (MemberInfo) List [index];
			}
		}

		public int Add (object value)
		{
			throw new NotSupportedException ();
		}

		public void Clear ()
		{
			throw new NotSupportedException ();
		}

		public bool Contains (object value)
		{
			return List.Contains (value);
		}

		public int IndexOf (object value)
		{
			return List.IndexOf (value);
		}

		public void Insert (int index, object value)
		{
			throw new NotSupportedException ();
		}

		public void Remove (object value)
		{
			throw new NotSupportedException ();
		}

		public void RemoveAt (int index)
		{
			throw new NotSupportedException ();
		}
	}

	/// <summary>
	///   This interface is used to get all members of a class when creating the
	///   member cache.  It must be implemented by all DeclSpace derivatives which
	///   want to support the member cache and by TypeHandle to get caching of
	///   non-dynamic types.
	/// </summary>
	public interface IMemberContainer {
		/// <summary>
		///   The name of the IMemberContainer.  This is only used for
		///   debugging purposes.
		/// </summary>
		string Name {
			get;
		}

		/// <summary>
		///   The type of this IMemberContainer.
		/// </summary>
		Type Type {
			get;
		}

		/// <summary>
		///   Returns the IMemberContainer of the base class or null if this
		///   is an interface or TypeManger.object_type.
		///   This is used when creating the member cache for a class to get all
		///   members from the base class.
		/// </summary>
		MemberCache BaseCache {
			get;
		}

		/// <summary>
		///   Whether this is an interface.
		/// </summary>
		bool IsInterface {
			get;
		}

		/// <summary>
		///   Returns all members of this class with the corresponding MemberTypes
		///   and BindingFlags.
		/// </summary>
		/// <remarks>
		///   When implementing this method, make sure not to return any inherited
		///   members and check the MemberTypes and BindingFlags properly.
		///   Unfortunately, System.Reflection is lame and doesn't provide a way to
		///   get the BindingFlags (static/non-static,public/non-public) in the
		///   MemberInfo class, but the cache needs this information.  That's why
		///   this method is called multiple times with different BindingFlags.
		/// </remarks>
		MemberList GetMembers (MemberTypes mt, BindingFlags bf);

		/// <summary>
		///   Return the container's member cache.
		/// </summary>
		MemberCache MemberCache {
			get;
		}
	}

	/// <summary>
	///   The MemberCache is used by dynamic and non-dynamic types to speed up
	///   member lookups.  It has a member name based hash table; it maps each member
	///   name to a list of CacheEntry objects.  Each CacheEntry contains a MemberInfo
	///   and the BindingFlags that were initially used to get it.  The cache contains
	///   all members of the current class and all inherited members.  If this cache is
	///   for an interface types, it also contains all inherited members.
	///
	///   There are two ways to get a MemberCache:
	///   * if this is a dynamic type, lookup the corresponding DeclSpace and then
	///     use the DeclSpace.MemberCache property.
	///   * if this not a dynamic type, call TypeHandle.GetTypeHandle() to get a
	///     TypeHandle instance for the type and then use TypeHandle.MemberCache.
	/// </summary>
	public class MemberCache {
		public readonly IMemberContainer Container;
		protected Hashtable member_hash;
		protected Hashtable method_hash;

		/// <summary>
		///   Create a new MemberCache for the given IMemberContainer `container'.
		/// </summary>
		public MemberCache (IMemberContainer container)
		{
			this.Container = container;

			Timer.IncrementCounter (CounterType.MemberCache);
			Timer.StartTimer (TimerType.CacheInit);

			// If we have a base class (we have a base class unless we're
			// TypeManager.object_type), we deep-copy its MemberCache here.
			if (Container.BaseCache != null)
				member_hash = SetupCache (Container.BaseCache);
			else
				member_hash = new Hashtable ();

			// If this is neither a dynamic type nor an interface, create a special
			// method cache with all declared and inherited methods.
			Type type = container.Type;
			if (!(type is TypeBuilder) && !type.IsInterface &&
			    (Container.BaseCache == null || Container.BaseCache.method_hash != null)) {
				method_hash = new Hashtable ();
				AddMethods (type);
			}

			// Add all members from the current class.
			AddMembers (Container);

			Timer.StopTimer (TimerType.CacheInit);
		}

		public MemberCache (Type[] ifaces)
		{
			//
			// The members of this cache all belong to other caches.  
			// So, 'Container' will not be used.
			//
			this.Container = null;

			member_hash = new Hashtable ();
			if (ifaces == null)
				return;

			foreach (Type itype in ifaces)
				AddCacheContents (TypeManager.LookupMemberCache (itype));
		}

		/// <summary>
		///   Bootstrap this member cache by doing a deep-copy of our base.
		/// </summary>
		static Hashtable SetupCache (MemberCache base_class)
		{
			Hashtable hash = new Hashtable ();

			if (base_class == null)
				return hash;

			IDictionaryEnumerator it = base_class.member_hash.GetEnumerator ();
			while (it.MoveNext ()) {
				hash [it.Key] = ((ArrayList) it.Value).Clone ();
			 }
                                
			return hash;
		}

		/// <summary>
		///   Add the contents of `cache' to the member_hash.
		/// </summary>
		void AddCacheContents (MemberCache cache)
		{
			IDictionaryEnumerator it = cache.member_hash.GetEnumerator ();
			while (it.MoveNext ()) {
				ArrayList list = (ArrayList) member_hash [it.Key];
				if (list == null)
					member_hash [it.Key] = list = new ArrayList ();

				ArrayList entries = (ArrayList) it.Value;
				for (int i = entries.Count-1; i >= 0; i--) {
					CacheEntry entry = (CacheEntry) entries [i];

					if (entry.Container != cache.Container)
						break;
					list.Add (entry);
				}
			}
		}

		/// <summary>
		///   Add all members from class `container' to the cache.
		/// </summary>
		void AddMembers (IMemberContainer container)
		{
			// We need to call AddMembers() with a single member type at a time
			// to get the member type part of CacheEntry.EntryType right.
			if (!container.IsInterface) {
				AddMembers (MemberTypes.Constructor, container);
				AddMembers (MemberTypes.Field, container);
			}
			AddMembers (MemberTypes.Method, container);
			AddMembers (MemberTypes.Property, container);
			AddMembers (MemberTypes.Event, container);
			// Nested types are returned by both Static and Instance searches.
			AddMembers (MemberTypes.NestedType,
				    BindingFlags.Static | BindingFlags.Public, container);
			AddMembers (MemberTypes.NestedType,
				    BindingFlags.Static | BindingFlags.NonPublic, container);
		}

		void AddMembers (MemberTypes mt, IMemberContainer container)
		{
			AddMembers (mt, BindingFlags.Static | BindingFlags.Public, container);
			AddMembers (mt, BindingFlags.Static | BindingFlags.NonPublic, container);
			AddMembers (mt, BindingFlags.Instance | BindingFlags.Public, container);
			AddMembers (mt, BindingFlags.Instance | BindingFlags.NonPublic, container);
		}

		/// <summary>
		///   Add all members from class `container' with the requested MemberTypes and
		///   BindingFlags to the cache.  This method is called multiple times with different
		///   MemberTypes and BindingFlags.
		/// </summary>
		void AddMembers (MemberTypes mt, BindingFlags bf, IMemberContainer container)
		{
			MemberList members = container.GetMembers (mt, bf);

			foreach (MemberInfo member in members) {
				string name = member.Name;

				// We use a name-based hash table of ArrayList's.
				ArrayList list = (ArrayList) member_hash [name];
				if (list == null) {
					list = new ArrayList ();
					member_hash.Add (name, list);
				}

				// When this method is called for the current class, the list will
				// already contain all inherited members from our base classes.
				// We cannot add new members in front of the list since this'd be an
				// expensive operation, that's why the list is sorted in reverse order
				// (ie. members from the current class are coming last).
				list.Add (new CacheEntry (container, member, mt, bf));
			}
		}

		/// <summary>
		///   Add all declared and inherited methods from class `type' to the method cache.
		/// </summary>
		void AddMethods (Type type)
		{
			AddMethods (BindingFlags.Static | BindingFlags.Public |
				    BindingFlags.FlattenHierarchy, type);
			AddMethods (BindingFlags.Static | BindingFlags.NonPublic |
				    BindingFlags.FlattenHierarchy, type);
			AddMethods (BindingFlags.Instance | BindingFlags.Public, type);
			AddMethods (BindingFlags.Instance | BindingFlags.NonPublic, type);
		}

		static ArrayList overrides = new ArrayList ();

		void AddMethods (BindingFlags bf, Type type)
		{
			MethodBase [] members = type.GetMethods (bf);

                        Array.Reverse (members);

			foreach (MethodBase member in members) {
				string name = member.Name;

				// We use a name-based hash table of ArrayList's.
				ArrayList list = (ArrayList) method_hash [name];
				if (list == null) {
					list = new ArrayList ();
					method_hash.Add (name, list);
				}

				MethodInfo curr = (MethodInfo) member;
				while (curr.IsVirtual && (curr.Attributes & MethodAttributes.NewSlot) == 0) {
					MethodInfo base_method = curr.GetBaseDefinition ();

					if (base_method == curr)
						// Not every virtual function needs to have a NewSlot flag.
						break;

					overrides.Add (curr);
					list.Add (new CacheEntry (null, base_method, MemberTypes.Method, bf));
					curr = base_method;
				}

				if (overrides.Count > 0) {
					for (int i = 0; i < overrides.Count; ++i)
						TypeManager.RegisterOverride ((MethodBase) overrides [i], curr);
					overrides.Clear ();
				}

				// Unfortunately, the elements returned by Type.GetMethods() aren't
				// sorted so we need to do this check for every member.
				BindingFlags new_bf = bf;
				if (member.DeclaringType == type)
					new_bf |= BindingFlags.DeclaredOnly;

				list.Add (new CacheEntry (Container, member, MemberTypes.Method, new_bf));
			}
		}

		/// <summary>
		///   Compute and return a appropriate `EntryType' magic number for the given
		///   MemberTypes and BindingFlags.
		/// </summary>
		protected static EntryType GetEntryType (MemberTypes mt, BindingFlags bf)
		{
			EntryType type = EntryType.None;

			if ((mt & MemberTypes.Constructor) != 0)
				type |= EntryType.Constructor;
			if ((mt & MemberTypes.Event) != 0)
				type |= EntryType.Event;
			if ((mt & MemberTypes.Field) != 0)
				type |= EntryType.Field;
			if ((mt & MemberTypes.Method) != 0)
				type |= EntryType.Method;
			if ((mt & MemberTypes.Property) != 0)
				type |= EntryType.Property;
			// Nested types are returned by static and instance searches.
			if ((mt & MemberTypes.NestedType) != 0)
				type |= EntryType.NestedType | EntryType.Static | EntryType.Instance;

			if ((bf & BindingFlags.Instance) != 0)
				type |= EntryType.Instance;
			if ((bf & BindingFlags.Static) != 0)
				type |= EntryType.Static;
			if ((bf & BindingFlags.Public) != 0)
				type |= EntryType.Public;
			if ((bf & BindingFlags.NonPublic) != 0)
				type |= EntryType.NonPublic;
			if ((bf & BindingFlags.DeclaredOnly) != 0)
				type |= EntryType.Declared;

			return type;
		}

		/// <summary>
		///   The `MemberTypes' enumeration type is a [Flags] type which means that it may
		///   denote multiple member types.  Returns true if the given flags value denotes a
		///   single member types.
		/// </summary>
		public static bool IsSingleMemberType (MemberTypes mt)
		{
			switch (mt) {
			case MemberTypes.Constructor:
			case MemberTypes.Event:
			case MemberTypes.Field:
			case MemberTypes.Method:
			case MemberTypes.Property:
			case MemberTypes.NestedType:
				return true;

			default:
				return false;
			}
		}

		/// <summary>
		///   We encode the MemberTypes and BindingFlags of each members in a "magic"
		///   number to speed up the searching process.
		/// </summary>
		[Flags]
		protected enum EntryType {
			None		= 0x000,

			Instance	= 0x001,
			Static		= 0x002,
			MaskStatic	= Instance|Static,

			Public		= 0x004,
			NonPublic	= 0x008,
			MaskProtection	= Public|NonPublic,

			Declared	= 0x010,

			Constructor	= 0x020,
			Event		= 0x040,
			Field		= 0x080,
			Method		= 0x100,
			Property	= 0x200,
			NestedType	= 0x400,

			MaskType	= Constructor|Event|Field|Method|Property|NestedType
		}

		protected class CacheEntry {
			public readonly IMemberContainer Container;
			public readonly EntryType EntryType;
			public readonly MemberInfo Member;

			public CacheEntry (IMemberContainer container, MemberInfo member,
					   MemberTypes mt, BindingFlags bf)
			{
				this.Container = container;
				this.Member = member;
				this.EntryType = GetEntryType (mt, bf);
			}

			public override string ToString ()
			{
				return String.Format ("CacheEntry ({0}:{1}:{2})", Container.Name,
						      EntryType, Member);
			}
		}

		/// <summary>
		///   This is called each time we're walking up one level in the class hierarchy
		///   and checks whether we can abort the search since we've already found what
		///   we were looking for.
		/// </summary>
		protected bool DoneSearching (ArrayList list)
		{
			//
			// We've found exactly one member in the current class and it's not
			// a method or constructor.
			//
			if (list.Count == 1 && !(list [0] is MethodBase))
				return true;

			//
			// Multiple properties: we query those just to find out the indexer
			// name
			//
			if ((list.Count > 0) && (list [0] is PropertyInfo))
				return true;

			return false;
		}

		/// <summary>
		///   Looks up members with name `name'.  If you provide an optional
		///   filter function, it'll only be called with members matching the
		///   requested member name.
		///
		///   This method will try to use the cache to do the lookup if possible.
		///
		///   Unlike other FindMembers implementations, this method will always
		///   check all inherited members - even when called on an interface type.
		///
		///   If you know that you're only looking for methods, you should use
		///   MemberTypes.Method alone since this speeds up the lookup a bit.
		///   When doing a method-only search, it'll try to use a special method
		///   cache (unless it's a dynamic type or an interface) and the returned
		///   MemberInfo's will have the correct ReflectedType for inherited methods.
		///   The lookup process will automatically restart itself in method-only
		///   search mode if it discovers that it's about to return methods.
		/// </summary>
		ArrayList global = new ArrayList ();
		bool using_global = false;
		
		static MemberInfo [] emptyMemberInfo = new MemberInfo [0];
		
		public MemberInfo [] FindMembers (MemberTypes mt, BindingFlags bf, string name,
						  MemberFilter filter, object criteria)
		{
			if (using_global)
				throw new Exception ();
			
			bool declared_only = (bf & BindingFlags.DeclaredOnly) != 0;
			bool method_search = mt == MemberTypes.Method;
			// If we have a method cache and we aren't already doing a method-only search,
			// then we restart a method search if the first match is a method.
			bool do_method_search = !method_search && (method_hash != null);

			ArrayList applicable;

			// If this is a method-only search, we try to use the method cache if
			// possible; a lookup in the method cache will return a MemberInfo with
			// the correct ReflectedType for inherited methods.
			
			if (method_search && (method_hash != null))
				applicable = (ArrayList) method_hash [name];
			else
				applicable = (ArrayList) member_hash [name];

			if (applicable == null)
				return emptyMemberInfo;

			//
			// 32  slots gives 53 rss/54 size
			// 2/4 slots gives 55 rss
			//
			// Strange: from 25,000 calls, only 1,800
			// are above 2.  Why does this impact it?
			//
			global.Clear ();
			using_global = true;

			Timer.StartTimer (TimerType.CachedLookup);

			EntryType type = GetEntryType (mt, bf);

			IMemberContainer current = Container;


			// `applicable' is a list of all members with the given member name `name'
			// in the current class and all its base classes.  The list is sorted in
			// reverse order due to the way how the cache is initialy created (to speed
			// things up, we're doing a deep-copy of our base).

			for (int i = applicable.Count-1; i >= 0; i--) {
				CacheEntry entry = (CacheEntry) applicable [i];

				// This happens each time we're walking one level up in the class
				// hierarchy.  If we're doing a DeclaredOnly search, we must abort
				// the first time this happens (this may already happen in the first
				// iteration of this loop if there are no members with the name we're
				// looking for in the current class).
				if (entry.Container != current) {
					if (declared_only || DoneSearching (global))
						break;

					current = entry.Container;
				}

				// Is the member of the correct type ?
				if ((entry.EntryType & type & EntryType.MaskType) == 0)
					continue;

				// Is the member static/non-static ?
				if ((entry.EntryType & type & EntryType.MaskStatic) == 0)
					continue;

				// Apply the filter to it.
				if (filter (entry.Member, criteria)) {
					if ((entry.EntryType & EntryType.MaskType) != EntryType.Method)
						do_method_search = false;
					global.Add (entry.Member);
				}
			}

			Timer.StopTimer (TimerType.CachedLookup);

			// If we have a method cache and we aren't already doing a method-only
			// search, we restart in method-only search mode if the first match is
			// a method.  This ensures that we return a MemberInfo with the correct
			// ReflectedType for inherited methods.
			if (do_method_search && (global.Count > 0)){
				using_global = false;

				return FindMembers (MemberTypes.Method, bf, name, filter, criteria);
			}

			using_global = false;
			MemberInfo [] copy = new MemberInfo [global.Count];
			global.CopyTo (copy);
			return copy;
		}
		
		// find the nested type @name in @this.
		public Type FindNestedType (string name)
		{
			ArrayList applicable = (ArrayList) member_hash [name];
			if (applicable == null)
				return null;
			
			for (int i = applicable.Count-1; i >= 0; i--) {
				CacheEntry entry = (CacheEntry) applicable [i];
				if ((entry.EntryType & EntryType.NestedType & EntryType.MaskType) != 0)
					return (Type) entry.Member;
			}
			
			return null;
		}
		
		//
		// This finds the method or property for us to override. invocationType is the type where
		// the override is going to be declared, name is the name of the method/property, and
		// paramTypes is the parameters, if any to the method or property
		//
		// Because the MemberCache holds members from this class and all the base classes,
		// we can avoid tons of reflection stuff.
		//
		public MemberInfo FindMemberToOverride (Type invocationType, string name, Type [] paramTypes, bool is_property)
		{
			ArrayList applicable;
			if (method_hash != null && !is_property)
				applicable = (ArrayList) method_hash [name];
			else
				applicable = (ArrayList) member_hash [name];
			
			if (applicable == null)
				return null;
			//
			// Walk the chain of methods, starting from the top.
			//
			for (int i = applicable.Count - 1; i >= 0; i--) {
				CacheEntry entry = (CacheEntry) applicable [i];
				
				if ((entry.EntryType & (is_property ? (EntryType.Property | EntryType.Field) : EntryType.Method)) == 0)
					continue;

				PropertyInfo pi = null;
				MethodInfo mi = null;
				FieldInfo fi = null;
				Type [] cmpAttrs = null;
				
				if (is_property) {
					if ((entry.EntryType & EntryType.Field) != 0) {
						fi = (FieldInfo)entry.Member;

						// TODO: For this case we ignore member type
						//fb = TypeManager.GetField (fi);
						//cmpAttrs = new Type[] { fb.MemberType };
					} else {
						pi = (PropertyInfo) entry.Member;
						cmpAttrs = TypeManager.GetArgumentTypes (pi);
					}
				} else {
					mi = (MethodInfo) entry.Member;
					cmpAttrs = TypeManager.GetParameterData (mi).Types;
				}

				if (fi != null) {
					// TODO: Almost duplicate !
					// Check visibility
					switch (fi.Attributes & FieldAttributes.FieldAccessMask) {
						case FieldAttributes.Private:
							//
							// A private method is Ok if we are a nested subtype.
							// The spec actually is not very clear about this, see bug 52458.
							//
							if (invocationType != entry.Container.Type &
								TypeManager.IsNestedChildOf (invocationType, entry.Container.Type))
								continue;

							break;
						case FieldAttributes.FamANDAssem:
						case FieldAttributes.Assembly:
							//
							// Check for assembly methods
							//
							if (mi.DeclaringType.Assembly != CodeGen.Assembly.Builder)
								continue;
							break;
					}
					return entry.Member;
				}

				//
				// Check the arguments
				//
				if (cmpAttrs.Length != paramTypes.Length)
					continue;
	
				for (int j = cmpAttrs.Length - 1; j >= 0; j --)
					if (paramTypes [j] != cmpAttrs [j])
						goto next;
				
				//
				// get one of the methods because this has the visibility info.
				//
				if (is_property) {
					mi = pi.GetGetMethod (true);
					if (mi == null)
						mi = pi.GetSetMethod (true);
				}
				
				//
				// Check visibility
				//
				switch (mi.Attributes & MethodAttributes.MemberAccessMask) {
				case MethodAttributes.Private:
					//
					// A private method is Ok if we are a nested subtype.
					// The spec actually is not very clear about this, see bug 52458.
					//
					if (invocationType == entry.Container.Type ||
					    TypeManager.IsNestedChildOf (invocationType, entry.Container.Type))
						return entry.Member;
					
					break;
				case MethodAttributes.FamANDAssem:
				case MethodAttributes.Assembly:
					//
					// Check for assembly methods
					//
					if (mi.DeclaringType.Assembly == CodeGen.Assembly.Builder)
						return entry.Member;
					
					break;
				default:
					//
					// A protected method is ok, because we are overriding.
					// public is always ok.
					//
					return entry.Member;
				}
			next:
				;
			}
			
			return null;
		}

 		/// <summary>
 		/// The method is looking for conflict with inherited symbols (errors CS0108, CS0109).
 		/// We handle two cases. The first is for types without parameters (events, field, properties).
 		/// The second are methods, indexers and this is why ignore_complex_types is here.
 		/// The latest param is temporary hack. See DoDefineMembers method for more info.
 		/// </summary>
 		public MemberInfo FindMemberWithSameName (string name, bool ignore_complex_types, MemberInfo ignore_member)
 		{
 			ArrayList applicable = null;
 
 			if (method_hash != null)
 				applicable = (ArrayList) method_hash [name];
 
 			if (applicable != null) {
 				for (int i = applicable.Count - 1; i >= 0; i--) {
 					CacheEntry entry = (CacheEntry) applicable [i];
 					if ((entry.EntryType & EntryType.Public) != 0)
 						return entry.Member;
 				}
 			}
 
 			if (member_hash == null)
 				return null;
 			applicable = (ArrayList) member_hash [name];
 			
 			if (applicable != null) {
 				for (int i = applicable.Count - 1; i >= 0; i--) {
 					CacheEntry entry = (CacheEntry) applicable [i];
 					if ((entry.EntryType & EntryType.Public) != 0 & entry.Member != ignore_member) {
 						if (ignore_complex_types) {
 							if ((entry.EntryType & EntryType.Method) != 0)
 								continue;
 
 							// Does exist easier way how to detect indexer ?
 							if ((entry.EntryType & EntryType.Property) != 0) {
 								Type[] arg_types = TypeManager.GetArgumentTypes ((PropertyInfo)entry.Member);
 								if (arg_types.Length > 0)
 									continue;
 							}
 						}
 						return entry.Member;
 					}
 				}
 			}
  			return null;
  		}

 		Hashtable locase_table;
 
 		/// <summary>
 		/// Builds low-case table for CLS Compliance test
 		/// </summary>
 		public Hashtable GetPublicMembers ()
 		{
 			if (locase_table != null)
 				return locase_table;
 
 			locase_table = new Hashtable ();
 			foreach (DictionaryEntry entry in member_hash) {
 				ArrayList members = (ArrayList)entry.Value;
 				for (int ii = 0; ii < members.Count; ++ii) {
 					CacheEntry member_entry = (CacheEntry) members [ii];
 
 					if ((member_entry.EntryType & EntryType.Public) == 0)
 						continue;
 
 					// TODO: Does anyone know easier way how to detect that member is internal ?
 					switch (member_entry.EntryType & EntryType.MaskType) {
 						case EntryType.Constructor:
 							continue;
 
 						case EntryType.Field:
 							if ((((FieldInfo)member_entry.Member).Attributes & (FieldAttributes.Assembly | FieldAttributes.Public)) == FieldAttributes.Assembly)
 								continue;
 							break;
 
 						case EntryType.Method:
 							if ((((MethodInfo)member_entry.Member).Attributes & (MethodAttributes.Assembly | MethodAttributes.Public)) == MethodAttributes.Assembly)
 								continue;
 							break;
 
 						case EntryType.Property:
 							PropertyInfo pi = (PropertyInfo)member_entry.Member;
 							if (pi.GetSetMethod () == null && pi.GetGetMethod () == null)
 								continue;
 							break;
 
 						case EntryType.Event:
 							EventInfo ei = (EventInfo)member_entry.Member;
 							MethodInfo mi = ei.GetAddMethod ();
 							if ((mi.Attributes & (MethodAttributes.Assembly | MethodAttributes.Public)) == MethodAttributes.Assembly)
 								continue;
 							break;
 					}
 					string lcase = ((string)entry.Key).ToLower (System.Globalization.CultureInfo.InvariantCulture);
 					locase_table [lcase] = member_entry.Member;
 					break;
 				}
 			}
 			return locase_table;
 		}
 
 		public Hashtable Members {
 			get {
 				return member_hash;
 			}
 		}
 
 		/// <summary>
 		/// Cls compliance check whether methods or constructors parameters differing only in ref or out, or in array rank
 		/// </summary>
 		/// 
		// TODO: refactor as method is always 'this'
 		public static void VerifyClsParameterConflict (ArrayList al, MethodCore method, MemberInfo this_builder)
 		{
 			EntryType tested_type = (method is Constructor ? EntryType.Constructor : EntryType.Method) | EntryType.Public;
 
 			for (int i = 0; i < al.Count; ++i) {
 				MemberCache.CacheEntry entry = (MemberCache.CacheEntry) al [i];
 		
 				// skip itself
 				if (entry.Member == this_builder)
 					continue;
 		
 				if ((entry.EntryType & tested_type) != tested_type)
 					continue;
 		
				MethodBase method_to_compare = (MethodBase)entry.Member;
				AttributeTester.Result result = AttributeTester.AreOverloadedMethodParamsClsCompliant (
					method.ParameterTypes, TypeManager.GetParameterData (method_to_compare).Types);

 				if (result == AttributeTester.Result.Ok)
 					continue;

				IMethodData md = TypeManager.GetMethod (method_to_compare);

				// TODO: now we are ignoring CLSCompliance(false) on method from other assembly which is buggy.
				// However it is exactly what csc does.
				if (md != null && !md.IsClsComplianceRequired ())
					continue;
 		
 				Report.SymbolRelatedToPreviousError (entry.Member);
				switch (result) {
					case AttributeTester.Result.RefOutArrayError:
						Report.Error (3006, method.Location, "Overloaded method `{0}' differing only in ref or out, or in array rank, is not CLS-compliant", method.GetSignatureForError ());
						continue;
					case AttributeTester.Result.ArrayArrayError:
						Report.Error (3007, method.Location, "Overloaded method `{0}' differing only by unnamed array types is not CLS-compliant", method.GetSignatureForError ());
						continue;
				}

				throw new NotImplementedException (result.ToString ());
 			}
  		}
	}
}
