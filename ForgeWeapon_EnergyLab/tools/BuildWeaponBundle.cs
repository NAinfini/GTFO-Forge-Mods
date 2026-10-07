using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Unity 2019.4.21f1 format-4 bundles, one per gun: its prefab of part meshes, its texture sets and its
/// own presentation-format/presentation-profile, the same layout a third-party model bundle uses. No materials.</summary>
public static class ForgeEnergyModelBuild
{
    [Serializable] public sealed class Part {public string name,surface,textureSet,mesh,sha256;public Vector3 pivot;public Color color;public int triangles;}
    [Serializable] public sealed class Weapon {public string mode,sight,rail;public float length;public Vector3 rightHand,leftHand,muzzle,sightLook,boundsMin,boundsMax;public Part[] parts;}
    [Serializable] public sealed class TextureSet {public string name;public bool emission,normal;}
    [Serializable] public sealed class Contract {public int format;public Weapon[] weapons;public TextureSet[] textureSets;}
    [Serializable] public sealed class PartSummary {public string name,surface,textureSet;public int triangles;}
    [Serializable] public sealed class WeaponSummary {public string mode,sight,rail;public float length;public int triangles;public PartSummary[] parts;}
    [Serializable] public sealed class TextureEvidence {public string asset,format;public int width,height,mipCount;}
    [Serializable] public sealed class BundleEvidence {public string bundle,sha256;public long bytes;public WeaponSummary weapon;public TextureEvidence[] textures;}
    [Serializable] public sealed class Manifest {public string unity;public int format;public BundleEvidence[] bundles;}
    const long BundleBudget=80L*1024*1024;
    static string BundleName(string mode)=>"forge-energy-model-"+mode+".bundle";
    static readonly string[] Modes={"beam","arc","plasma-blast","plasma-arc","blast","disc","flame","hole"};
    static readonly string[] Required={"Receiver","Rail","Front","Stock","Magazine","Sight"};
    static readonly string[] Surfaces={"body","glass","reticle"};
    static readonly string[] Sockets={"RightHand","LeftHand","Muzzle","SightLook"};
    static readonly string[] Kinds={"albedo","metallic","emission","normal"};
    static bool Has(TextureSet s,string kind)=>kind=="emission"?s.emission:kind=="normal"?s.normal:true;
    public static void Build() {try{BuildChecked();EditorApplication.Exit(0);}catch(Exception e){Debug.LogException(e);EditorApplication.Exit(1);}}
    static void BuildChecked()
    {
        if(Application.unityVersion!="2019.4.21f1")throw new InvalidOperationException("Use the game's Unity 2019.4.21f1.");
        var project=Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        var output=Environment.GetEnvironmentVariable("FORGE_ENERGY_PRESENTATION_OUTPUT");
        if(string.IsNullOrWhiteSpace(output))throw new InvalidDataException("Set FORGE_ENERGY_PRESENTATION_OUTPUT to the candidate output directory.");
        output=Path.GetFullPath(output);
        var geometry=Path.Combine(project,"Geometry");
        var contract=JsonUtility.FromJson<Contract>(File.ReadAllText(Path.Combine(geometry,"manifest.json")));
        Validate(contract,geometry,project);
        var prefabs=new Dictionary<string,string>();
        Directory.CreateDirectory(Path.Combine(project,"Assets/Generated"));AssetDatabase.Refresh();
        foreach(var w in contract.weapons)
        {
            var root=new GameObject("energy-model-"+w.mode);
            try
            {
                foreach(var part in w.parts)
                {
                    var mesh=ReadMesh(Path.Combine(geometry,part.mesh));mesh.name=w.mode+"-"+part.name;
                    if(mesh.triangles.Length/3!=part.triangles)throw new InvalidDataException("Triangle count differs: "+w.mode+"/"+part.name);
                    if(part.surface=="body")
                    {
                        var fraction=WindingFraction(mesh);
                        if(fraction<.9)throw new InvalidDataException(w.mode+"/"+part.name+": only "+fraction.ToString("F4")+" of the area is wound clockwise toward its normals.");
                    }
                    // Glass has no texture; the unlit reticle only consumes positions and its runtime tint.
                    if(part.surface!="body")mesh.uv=null;
                    if(part.surface=="reticle")mesh.normals=null;
                    // Unity's Low mesh compression perturbs some normals by nearly 3 degrees.
                    // Use its native half-float vertex format instead, preserving positions and UVs exactly.
                    if(part.surface=="body")PackNormals(mesh);
                    if(part.surface!="reticle")mesh.UploadMeshData(true);
                    AssetDatabase.CreateAsset(mesh,"Assets/Generated/"+w.mode+"-"+part.name+".asset");
                    var go=new GameObject(part.name);go.transform.SetParent(root.transform,false);go.transform.localPosition=part.pivot;
                    go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterials=new Material[0];
                }
                Socket(root,"RightHand",w.rightHand);Socket(root,"LeftHand",w.leftHand);Socket(root,"Muzzle",w.muzzle);Socket(root,"SightLook",w.sightLook);
                var prefab="Assets/Generated/"+w.mode+".prefab";PrefabUtility.SaveAsPrefabAsset(root,prefab);prefabs.Add(w.mode,prefab);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        // An asset can belong to one bundle only. Each bundle gets its own profile/format TextAssets, and a texture
        // set shared by several guns (the rails) is imported once per gun from a copy.
        var users=contract.textureSets.ToDictionary(s=>s.name,s=>contract.weapons.Count(w=>w.parts.Any(p=>p.textureSet==s.name)));
        var subsets=new Dictionary<string,Contract>();
        foreach(var w in contract.weapons)
        {
            var used=new HashSet<string>(w.parts.Where(p=>p.surface=="body").Select(p=>p.textureSet));
            var sub=new Contract{format=4,weapons=new[]{w},textureSets=contract.textureSets.Where(s=>used.Contains(s.name)).ToArray()};
            subsets.Add(w.mode,sub);
            var folder=Path.Combine(project,"Assets/Generated",w.mode);Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"presentation-format.txt"),"4");
            File.WriteAllText(Path.Combine(folder,"presentation-profile.json"),JsonUtility.ToJson(sub,true));
            foreach(var s in sub.textureSets.Where(s=>users[s.name]>1))
            {
                var copy=Path.Combine(folder,s.name);Directory.CreateDirectory(copy);
                foreach(var kind in Kinds)if(Has(s,kind))File.Copy(Path.Combine(project,"Assets/Weapons",s.name,kind+".png"),Path.Combine(copy,kind+".png"),true);
            }
        }
        AssetDatabase.Refresh();
        var builds=new List<AssetBundleBuild>();
        foreach(var w in contract.weapons)
        {
            var sub=subsets[w.mode];var generated="Assets/Generated/"+w.mode+"/";
            var textures=ImportTextures(sub.textureSets.Select(s=>users[s.name]>1?generated+s.name:"Assets/Weapons/"+s.name),sub);
            var paths=new[]{prefabs[w.mode],generated+"presentation-format.txt",generated+"presentation-profile.json"}.Concat(textures).ToArray();
            var names=new[]{w.mode,"presentation-format","presentation-profile"}.Concat(textures.Select(TextureName)).ToArray();
            builds.Add(new AssetBundleBuild{assetBundleName=BundleName(w.mode),assetNames=paths,addressableNames=names});
        }
        var staging=Path.Combine(project,"Library/EnergyModelBuild4");
        if(Directory.Exists(staging))Directory.Delete(staging,true);
        Directory.CreateDirectory(staging);AssetDatabase.SaveAssets();
        if(BuildPipeline.BuildAssetBundles(staging,builds.ToArray(),BuildAssetBundleOptions.ChunkBasedCompression|BuildAssetBundleOptions.StrictMode|BuildAssetBundleOptions.ForceRebuildAssetBundle,BuildTarget.StandaloneWindows64)==null)
            throw new InvalidOperationException("Unity bundle build failed.");
        Directory.CreateDirectory(output);
        var evidence=new List<BundleEvidence>();
        foreach(var w in contract.weapons)
        {
            var name=BundleName(w.mode);var file=Path.Combine(staging,name);var bytes=new FileInfo(file).Length;
            if(bytes>BundleBudget)throw new InvalidDataException(name+" exceeds the size budget; do not silently lower texture quality.");
            var textures=Verify(file,subsets[w.mode]);
            var destination=Path.Combine(output,name);File.Copy(file,destination,true);
            var hash=Hash(destination);
            evidence.Add(new BundleEvidence{bundle=name,sha256=hash,bytes=bytes,textures=textures,
                weapon=new WeaponSummary{mode=w.mode,sight=w.sight,rail=w.rail,length=w.length,triangles=w.parts.Sum(p=>p.triangles),
                    parts=w.parts.Select(p=>new PartSummary{name=p.name,surface=p.surface,textureSet=p.textureSet,triangles=p.triangles}).ToArray()}});
            Debug.Log("ENERGY_BUNDLE_VERIFIED "+name+" "+bytes+" bytes "+hash);
        }
        File.WriteAllText(Path.Combine(output,"weapon-models-manifest.json"),JsonUtility.ToJson(new Manifest{unity=Application.unityVersion,format=4,bundles=evidence.ToArray()},true));
        ForgeEnergyDiscBuild.Build(output);
    }
    static void Validate(Contract c,string geometry,string project)
    {
        if(c==null||c.format!=4||c.weapons==null||c.textureSets==null||c.weapons.Length!=Modes.Length)throw new InvalidDataException("Expected a format-4 manifest with eight weapons.");
        var sets=new Dictionary<string,TextureSet>();
        foreach(var s in c.textureSets)
        {
            if(s==null||!Name(s.name,true)||sets.ContainsKey(s.name))throw new InvalidDataException("Invalid or duplicate texture set.");
            sets.Add(s.name,s);
            var folder=Path.Combine(project,"Assets/Weapons",s.name);
            foreach(var f in new[]{"albedo.png","metallic.png"})if(!File.Exists(Path.Combine(folder,f)))throw new FileNotFoundException(Path.Combine(folder,f));
            if(File.Exists(Path.Combine(folder,"emission.png"))!=s.emission)throw new InvalidDataException("emission.png and the manifest disagree: "+s.name);
            if(File.Exists(Path.Combine(folder,"normal.png"))!=s.normal)throw new InvalidDataException("normal.png and the manifest disagree: "+s.name);
        }
        var used=new HashSet<string>();var expected=new HashSet<string>(Modes);
        foreach(var w in c.weapons)
        {
            if(w==null||!expected.Remove(w.mode)||string.IsNullOrEmpty(w.sight)||string.IsNullOrEmpty(w.rail)||w.parts==null)throw new InvalidDataException("Invalid weapon record.");
            foreach(var v in new[]{w.rightHand,w.leftHand,w.muzzle,w.sightLook,w.boundsMin,w.boundsMax})if(!Finite(v))throw new InvalidDataException(w.mode+": non-finite socket or bounds.");
            if(!(w.length>=.5f&&w.length<=1.3f)||Mathf.Abs(w.length-(w.boundsMax.z-w.boundsMin.z))>1e-4f||w.rightHand!=Vector3.zero)throw new InvalidDataException(w.mode+": length/bounds/right hand inconsistent.");
            var seen=new HashSet<string>();
            foreach(var p in w.parts)
            {
                if(p==null||!Name(p.name,false)||!seen.Add(p.name)||Array.IndexOf(Surfaces,p.surface)<0||!Finite(p.pivot)||string.IsNullOrEmpty(p.mesh)||p.sha256==null||p.sha256.Length!=64||p.triangles<1)
                    throw new InvalidDataException(w.mode+": invalid part record.");
                if(p.surface=="body")
                {
                    if(!sets.ContainsKey(p.textureSet??""))throw new InvalidDataException(w.mode+"/"+p.name+": unknown texture set.");
                    if(p.color.r!=0||p.color.g!=0||p.color.b!=0||p.color.a!=0)throw new InvalidDataException(w.mode+"/"+p.name+": a body part carries no color.");
                    used.Add(p.textureSet);
                }
                else
                {
                    if(!string.IsNullOrEmpty(p.textureSet)||!Finite(p.color.r)||!Finite(p.color.g)||!Finite(p.color.b)||!Finite(p.color.a))throw new InvalidDataException(w.mode+"/"+p.name+": glass and reticle take a color and no texture set.");
                    if(p.surface=="glass"?p.name!="SightGlass":!p.name.StartsWith("SightReticle"))throw new InvalidDataException(w.mode+"/"+p.name+": unexpected sight part name.");
                }
                var full=Path.GetFullPath(Path.Combine(geometry,p.mesh));
                if(!full.StartsWith(Path.GetFullPath(geometry)+Path.DirectorySeparatorChar)||!File.Exists(full)||Hash(full)!=p.sha256)throw new InvalidDataException("Mesh missing or changed after export: "+w.mode+"/"+p.name);
            }
            foreach(var r in Required)if(!seen.Contains(r))throw new InvalidDataException(w.mode+": missing part "+r);
        }
        foreach(var s in c.textureSets)if(!used.Contains(s.name))throw new InvalidDataException("Texture set used by no part: "+s.name);
    }
    static bool Name(string n,bool allowDash){return !string.IsNullOrEmpty(n)&&n.All(ch=>char.IsLetterOrDigit(ch)||(allowDash&&ch=='-'));}
    static bool Finite(float v){return !float.IsNaN(v)&&!float.IsInfinity(v);}
    static bool Finite(Vector3 v){return Finite(v.x)&&Finite(v.y)&&Finite(v.z);}
    static string Hash(string file){using(var sha=SHA256.Create())using(var stream=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    static Mesh ReadMesh(string path)
    {
        using(var r=new BinaryReader(File.OpenRead(path)))
        {
            var n=r.ReadInt32();var m=r.ReadInt32();if(n<3||n>1000000||m<3||m>3000000||m%3!=0||r.BaseStream.Length!=8L+n*32L+m*4L)throw new InvalidDataException("Bad mesh header: "+path);
            var p=new Vector3[n];var normals=new Vector3[n];var uv=new Vector2[n];var indices=new int[m];
            for(var i=0;i<n;i++)
            {
                var a=new float[8];for(var j=0;j<8;j++){a[j]=r.ReadSingle();if(!Finite(a[j]))throw new InvalidDataException("Nonfinite vertex: "+path);}
                p[i]=new Vector3(a[0],a[1],a[2]);normals[i]=new Vector3(a[3],a[4],a[5]);uv[i]=new Vector2(a[6],a[7]);
                if(normals[i].sqrMagnitude<.25f)throw new InvalidDataException("Degenerate normal: "+path);
            }
            for(var i=0;i<m;i++){indices[i]=r.ReadInt32();if(indices[i]<0||indices[i]>=n)throw new InvalidDataException("Invalid index: "+path);}
            var mesh=new Mesh{indexFormat=n>65535?IndexFormat.UInt32:IndexFormat.UInt16};mesh.vertices=p;mesh.normals=normals;mesh.uv=uv;mesh.triangles=indices;mesh.RecalculateBounds();return mesh;
        }
    }
    /// <summary>Unity front faces are clockwise, so Cross(v1-v0,v2-v0) must point along the supplied normals.</summary>
    static double WindingFraction(Mesh mesh)
    {
        var v=mesh.vertices;var n=mesh.normals;var t=mesh.triangles;double good=0,all=0;
        for(var i=0;i<t.Length;i+=3)
        {
            var c=Vector3.Cross(v[t[i+1]]-v[t[i]],v[t[i+2]]-v[t[i]]);var area=c.magnitude;
            all+=area;if(Vector3.Dot(c,n[t[i]]+n[t[i+1]]+n[t[i+2]])>0)good+=area;
        }
        return all>0?good/all:0;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PackedVertex { public Vector3 position;public ushort nx,ny,nz,pad;public Vector2 uv; }
    static void PackNormals(Mesh mesh)
    {
        var p=mesh.vertices;var n=mesh.normals;var uv=mesh.uv;var v=new PackedVertex[p.Length];
        for(var i=0;i<v.Length;i++)v[i]=new PackedVertex{position=p[i],nx=Mathf.FloatToHalf(n[i].x),ny=Mathf.FloatToHalf(n[i].y),nz=Mathf.FloatToHalf(n[i].z),uv=uv[i]};
        mesh.SetVertexBufferParams(v.Length,new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
            new VertexAttributeDescriptor(VertexAttribute.Normal,VertexAttributeFormat.Float16,4),new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2));
        mesh.SetVertexBufferData(v,0,0,v.Length,0,MeshUpdateFlags.DontRecalculateBounds);
    }
    static void Socket(GameObject root,string name,Vector3 p){var go=new GameObject(name);go.transform.SetParent(root.transform,false);go.transform.localPosition=p;go.transform.localRotation=Quaternion.identity;}
    static string TextureName(string path){return Path.GetFileName(Path.GetDirectoryName(path))+"-"+Path.GetFileNameWithoutExtension(path);}
    // Front normal maps use 1024; closer parts retain 2048.
    static int MaxSize(string set,string kind)
    {
        if(kind!="normal")return 2048;
        return set.EndsWith("-Front")?1024:2048;
    }
    // folders[i] holds c.textureSets[i]; the folder name is the texture set name.
    static string[] ImportTextures(IEnumerable<string> folders,Contract c)
    {
        var paths=new List<string>();AssetDatabase.Refresh();
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach(var (s,folder) in c.textureSets.Zip(folders,(s,f)=>(s,f)))
                foreach(var kind in Kinds)
                {
                    if(!Has(s,kind))continue;
                    var path=folder+"/"+kind+".png";
                    var importer=AssetImporter.GetAtPath(path) as TextureImporter;if(importer==null)throw new FileNotFoundException(path);
                    // Normal maps are tangent-space OpenGL (+Y) imported as Unity normal maps; metallic and normal are linear.
                    var type=kind=="normal"?TextureImporterType.NormalMap:TextureImporterType.Default;
                    var srgb=kind=="albedo"||kind=="emission";var max=MaxSize(s.name,kind);
                    if(importer.textureType==type&&importer.sRGBTexture==srgb&&importer.mipmapEnabled&&!importer.isReadable&&importer.maxTextureSize==max
                        &&importer.textureCompression==TextureImporterCompression.Compressed&&importer.crunchedCompression&&importer.compressionQuality==75){paths.Add(path);continue;}
                    importer.textureType=type;importer.sRGBTexture=srgb;importer.mipmapEnabled=true;importer.isReadable=false;importer.maxTextureSize=max;
                    importer.textureCompression=TextureImporterCompression.Compressed;importer.crunchedCompression=true;importer.compressionQuality=75;
                    importer.SaveAndReimport();paths.Add(path);
                }
        }
        finally{AssetDatabase.StopAssetEditing();}
        AssetDatabase.Refresh();
        return paths.ToArray();
    }
    static TextureEvidence[] Verify(string file,Contract c)
    {
        var bundle=AssetBundle.LoadFromFile(file);
        if(bundle==null)throw new InvalidDataException("Bundle does not load.");
        try
        {
            var format=bundle.LoadAsset<TextAsset>("presentation-format");var profile=bundle.LoadAsset<TextAsset>("presentation-profile");
            if(format==null||format.text!="4"||profile==null)throw new InvalidDataException("Bundle verification failed: presentation TextAssets.");
            var loaded=JsonUtility.FromJson<Contract>(profile.text);
            if(loaded.format!=4||loaded.weapons.Length!=c.weapons.Length||loaded.weapons.Sum(w=>w.parts.Length)!=c.weapons.Sum(w=>w.parts.Length))throw new InvalidDataException("Bundled profile differs from the built manifest.");
            foreach(var w in c.weapons)
            {
                var prefab=bundle.LoadAsset<GameObject>(w.mode);
                if(prefab==null)throw new InvalidDataException("Prefab missing: "+w.mode);
                var renderers=prefab.GetComponentsInChildren<MeshRenderer>(true);
                if(renderers.Length!=w.parts.Length||prefab.transform.childCount!=w.parts.Length+Sockets.Length)throw new InvalidDataException(w.mode+": renderer/child count differs from the manifest.");
                foreach(var r in renderers)if(r.sharedMaterials.Any(m=>m!=null))throw new InvalidDataException(w.mode+": "+r.name+" carries a material.");
                foreach(var s in Sockets)if(prefab.transform.Find(s)==null||prefab.transform.Find(s).parent!=prefab.transform)throw new InvalidDataException(w.mode+": socket "+s+" missing.");
                foreach(var p in w.parts)
                {
                    var t=prefab.transform.Find(p.name);var mesh=t==null?null:t.GetComponent<MeshFilter>().sharedMesh;
                    if(t==null||t.parent!=prefab.transform||(t.localPosition-p.pivot).sqrMagnitude>1e-10f||mesh==null||mesh.GetIndexCount(0)/3!=p.triangles)throw new InvalidDataException(w.mode+"/"+p.name+": bundled part differs from the manifest.");
                    if(p.surface!="reticle"&&mesh.isReadable)throw new InvalidDataException(w.mode+"/"+p.name+": unnecessary CPU mesh copy.");
                    if(p.surface=="body"&&(mesh.GetVertexAttributeFormat(VertexAttribute.Normal)!=VertexAttributeFormat.Float16
                        ||mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0)!=VertexAttributeFormat.Float32))
                        throw new InvalidDataException(w.mode+"/"+p.name+": Unity changed the reviewed vertex precision.");
                }
            }
            var evidence=new List<TextureEvidence>();
            foreach(var s in c.textureSets)
                foreach(var kind in Kinds)
                {
                    var name=s.name+"-"+kind;var texture=bundle.LoadAsset<Texture2D>(name);
                    if(!Has(s,kind)){if(texture!=null)throw new InvalidDataException("Unexpected texture: "+name);continue;}
                    if(texture==null||texture.mipmapCount<2)throw new InvalidDataException("Bundled texture missing or without mips: "+name);
                    RequireBlockCompressed(texture);
                    evidence.Add(new TextureEvidence{asset=name,format=texture.format.ToString(),width=texture.width,height=texture.height,mipCount=texture.mipmapCount});
                }
            return evidence.ToArray();
        }
        finally{bundle.Unload(true);}
    }
    static void RequireBlockCompressed(Texture2D texture)
    {
        if(texture.format!=TextureFormat.DXT1&&texture.format!=TextureFormat.DXT1Crunched&&
            texture.format!=TextureFormat.DXT5&&texture.format!=TextureFormat.DXT5Crunched)
            throw new InvalidDataException("Bundled texture is not block-compressed: "+texture.name+" "+texture.format);
    }
}
