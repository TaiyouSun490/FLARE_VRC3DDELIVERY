using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarCatalog.Remote
{
    /// <summary>Converts a conservative GLB 2.0 static subset into RAC2.</summary>
    public static class Rac2GlbConverter
    {
        private const uint GlbMagic = 0x46546c67u;
        private const uint JsonChunk = 0x4e4f534au;
        private const uint BinChunk = 0x004e4942u;
        private const int MaximumSourceBytes = 50_000_000;

        [MenuItem("Tools/FLARE/Developer/Legacy Exporters/Convert GLB to RAC2...")]
        public static void ConvertInteractive()
        {
            string input = EditorUtility.OpenFilePanel("Select GLB 2.0", "", "glb");
            if (string.IsNullOrEmpty(input)) return;
            string output = EditorUtility.SaveFilePanel("Save RAC2", Path.GetDirectoryName(input), Path.GetFileNameWithoutExtension(input), "rac2");
            if (string.IsNullOrEmpty(output)) return;
            try
            {
                int interactionChoice = EditorUtility.DisplayDialogComplex(
                    "RAC2 Interaction",
                    "Choose whether the restored exhibit can be carried.",
                    "Portable (Pickup)",
                    "Fixed (Collider)",
                    "Cancel");
                if (interactionChoice == 2) return;
                var interaction = new Rac2BinaryExporter.InteractionData
                {
                    HasCollider = true,
                    IsPortable = interactionChoice == 0,
                };
                Rac2BinaryExporter.ProductData product = Rac2ProductMetadataUtility.From(null);
                Rac2BinaryExporter.ExportSummary result = Convert(input, output, interaction, product);
                EditorUtility.DisplayDialog("GLB to RAC2", "Exported " + result.VertexCount + " vertices / " + result.IndexCount + " indices / " + result.FileSize + " stored / " + result.UncompressedFileSize + " raw bytes / " + result.CompressedSectionCount + " LZ4 chunks.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("GLB to RAC2 failed", exception.Message, "OK");
            }
        }

        public static Rac2BinaryExporter.ExportSummary Convert(string glbPath, string rac2Path, Rac2BinaryExporter.InteractionData interaction = null, Rac2BinaryExporter.ProductData product = null)
        {
            byte[] file = File.ReadAllBytes(glbPath);
            if (file.Length < 20 || file.Length > MaximumSourceBytes) throw new InvalidDataException("GLB must be between 20 bytes and 50 MB.");
            uint magic=ReadU32(file,0), version=ReadU32(file,4), length=ReadU32(file,8);
            if(magic!=GlbMagic||version!=2u||length!=file.Length)throw new InvalidDataException("The file is not a canonical GLB 2.0 container.");
            byte[] jsonBytes=null,bin=null;int cursor=12;
            while(cursor<file.Length){if(cursor+8>file.Length)throw new InvalidDataException("GLB chunk header is truncated.");int chunkLength=checked((int)ReadU32(file,cursor));uint type=ReadU32(file,cursor+4);cursor+=8;if(chunkLength<0||cursor+chunkLength>file.Length)throw new InvalidDataException("GLB chunk extends beyond end-of-file.");byte[] chunk=new byte[chunkLength];Buffer.BlockCopy(file,cursor,chunk,0,chunkLength);cursor+=chunkLength;if(type==JsonChunk){if(jsonBytes!=null)throw new InvalidDataException("GLB contains multiple JSON chunks.");jsonBytes=chunk;}else if(type==BinChunk){if(bin!=null)throw new InvalidDataException("GLB contains multiple BIN chunks.");bin=chunk;}}
            if(jsonBytes==null||bin==null)throw new InvalidDataException("GLB must contain one JSON and one embedded BIN chunk.");
            string json=System.Text.Encoding.UTF8.GetString(jsonBytes).TrimEnd('\0',' ','\t','\r','\n');JObject root=JObject.Parse(json);
            JArray buffers=(JArray)root["buffers"];if(buffers==null||buffers.Count!=1||buffers[0]["uri"]!=null)throw new NotSupportedException("Only one embedded GLB buffer is supported in this RAC2 profile.");
            JArray views=(JArray)root["bufferViews"]??new JArray();JArray accessors=(JArray)root["accessors"]??new JArray();JArray meshes=(JArray)root["meshes"]??new JArray();JArray nodes=(JArray)root["nodes"]??new JArray();JArray scenes=(JArray)root["scenes"]??new JArray();
            int sceneIndex=(int?)root["scene"]??0;if(sceneIndex<0||sceneIndex>=scenes.Count)throw new InvalidDataException("GLB default scene is missing.");
            List<Vector3> positions=new List<Vector3>();List<Vector3> normals=new List<Vector3>();List<Vector2> uv0=new List<Vector2>();List<int> triangles=new List<int>();bool allNormals=true,allUv=true;int? materialIndex=null;
            JArray roots=(JArray)scenes[sceneIndex]["nodes"]??new JArray();foreach(JToken token in roots)AppendNode((int)token,Matrix4x4.identity,nodes,meshes,accessors,views,bin,positions,normals,uv0,triangles,ref allNormals,ref allUv,ref materialIndex,new HashSet<int>());
            if(positions.Count==0||triangles.Count==0)throw new InvalidDataException("GLB default scene contains no triangle geometry.");
            Mesh mesh=new Mesh{name=Path.GetFileNameWithoutExtension(glbPath)+" (RAC2)"};Texture2D texture=null;Texture2D normalTexture=null;Material material=null;
            try
            {
                mesh.indexFormat=positions.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16;mesh.SetVertices(positions);if(allNormals&&normals.Count==positions.Count)mesh.SetNormals(normals);if(allUv&&uv0.Count==positions.Count)mesh.SetUVs(0,uv0);mesh.SetTriangles(triangles,0,true);if(!allNormals)mesh.RecalculateNormals();mesh.RecalculateBounds();
                Color baseColor=Color.white;float normalScale=1f;int renderMode=0;float cutoff=.5f;
                if(materialIndex.HasValue)
                {
                    JArray materials=(JArray)root["materials"]??new JArray();if(materialIndex.Value<0||materialIndex.Value>=materials.Count)throw new InvalidDataException("GLB material index is invalid.");
                    JObject sourceMaterial=(JObject)materials[materialIndex.Value];JObject pbr=(JObject)sourceMaterial["pbrMetallicRoughness"];JArray factor=(JArray)pbr?["baseColorFactor"];if(factor!=null&&factor.Count==4)baseColor=new Color((float)factor[0],(float)factor[1],(float)factor[2],(float)factor[3]);
                    JToken textureToken=pbr?["baseColorTexture"]?["index"];if(textureToken!=null)texture=ReadEmbeddedTexture((int)textureToken,root,views,bin);
                    JToken normalToken=sourceMaterial["normalTexture"]?["index"];if(normalToken!=null){normalTexture=ReadEmbeddedTexture((int)normalToken,root,views,bin);normalScale=(float?)sourceMaterial["normalTexture"]?["scale"]??1f;}
                    string alphaMode=(string)sourceMaterial["alphaMode"]??"OPAQUE";if(alphaMode=="MASK"){renderMode=1;cutoff=(float?)sourceMaterial["alphaCutoff"]??.5f;}else if(alphaMode!="OPAQUE")throw new NotSupportedException("RAC2 v0.1 supports GLB OPAQUE and MASK materials only.");
                }
                Shader lilToon=Shader.Find("lilToon");if(lilToon==null)throw new InvalidOperationException("lilToon 2.3.4 or newer is required for RAC2 v0.1 export.");
                material=new Material(lilToon);material.SetColor("_Color",baseColor);material.SetFloat("_TransparentMode",renderMode);material.SetFloat("_Cutoff",cutoff);
                if(texture!=null)material.SetTexture("_MainTex",texture);
                if(normalTexture!=null){material.SetFloat("_UseBumpMap",1f);material.SetFloat("_BumpScale",normalScale);material.SetTexture("_BumpMap",normalTexture);mesh.RecalculateTangents();}
                return Rac2BinaryExporter.ExportMesh(mesh, material, rac2Path, texture, 1024, normalTexture, interaction: interaction, product: product);
            }
            finally{UnityEngine.Object.DestroyImmediate(mesh);if(material!=null)UnityEngine.Object.DestroyImmediate(material);if(texture!=null)UnityEngine.Object.DestroyImmediate(texture);if(normalTexture!=null)UnityEngine.Object.DestroyImmediate(normalTexture);}
        }

        private static void AppendNode(int index,Matrix4x4 parent,JArray nodes,JArray meshes,JArray accessors,JArray views,byte[] bin,List<Vector3> positions,List<Vector3> normals,List<Vector2> uv,List<int> triangles,ref bool allNormals,ref bool allUv,ref int? materialIndex,HashSet<int> stack)
        {
            if(index<0||index>=nodes.Count||!stack.Add(index))throw new InvalidDataException("GLB node graph is invalid or cyclic.");JObject node=(JObject)nodes[index];Matrix4x4 world=parent*NodeMatrix(node);JToken meshToken=node["mesh"];
            if(meshToken!=null){int meshIndex=(int)meshToken;if(meshIndex<0||meshIndex>=meshes.Count)throw new InvalidDataException("GLB mesh index is invalid.");JArray primitives=(JArray)meshes[meshIndex]["primitives"]??new JArray();foreach(JObject primitive in primitives)AppendPrimitive(primitive,world,accessors,views,bin,positions,normals,uv,triangles,ref allNormals,ref allUv,ref materialIndex);}
            JArray children=(JArray)node["children"];if(children!=null)foreach(JToken child in children)AppendNode((int)child,world,nodes,meshes,accessors,views,bin,positions,normals,uv,triangles,ref allNormals,ref allUv,ref materialIndex,stack);stack.Remove(index);
        }

        private static void AppendPrimitive(JObject primitive,Matrix4x4 matrix,JArray accessors,JArray views,byte[] bin,List<Vector3> positions,List<Vector3> normals,List<Vector2> uv,List<int> triangles,ref bool allNormals,ref bool allUv,ref int? materialIndex)
        {
            if(((int?)primitive["mode"]??4)!=4)throw new NotSupportedException("RAC2 conversion supports GLB triangle primitives only.");JObject attributes=(JObject)primitive["attributes"]??throw new InvalidDataException("GLB primitive attributes are missing.");
            int positionAccessor=(int?)attributes["POSITION"]??throw new InvalidDataException("GLB POSITION is required.");Vector3[] sourcePositions=ReadVec3(positionAccessor,accessors,views,bin);Vector3[] sourceNormals=attributes["NORMAL"]==null?null:ReadVec3((int)attributes["NORMAL"],accessors,views,bin);Vector2[] sourceUv=attributes["TEXCOORD_0"]==null?null:ReadVec2((int)attributes["TEXCOORD_0"],accessors,views,bin);if(sourceNormals!=null&&sourceNormals.Length!=sourcePositions.Length||sourceUv!=null&&sourceUv.Length!=sourcePositions.Length)throw new InvalidDataException("GLB attribute counts do not match POSITION.");
            int[] sourceIndices=primitive["indices"]==null?Sequential(sourcePositions.Length):ReadIndices((int)primitive["indices"],accessors,views,bin);if(sourceIndices.Length%3!=0)throw new InvalidDataException("GLB triangle index count is invalid.");
            int? currentMaterial=(int?)primitive["material"];if(materialIndex.HasValue&&currentMaterial!=materialIndex)throw new NotSupportedException("This RAC2 profile supports one BaseColor material. Atlas or split the GLB before conversion.");if(!materialIndex.HasValue)materialIndex=currentMaterial;
            int vertexBase=positions.Count;Matrix4x4 normalMatrix=matrix.inverse.transpose;
            for(int i=0;i<sourcePositions.Length;i++){Vector3 p=matrix.MultiplyPoint3x4(sourcePositions[i]);p.z=-p.z;positions.Add(p);if(sourceNormals!=null){Vector3 n=normalMatrix.MultiplyVector(sourceNormals[i]).normalized;n.z=-n.z;normals.Add(n);}else allNormals=false;if(sourceUv!=null)uv.Add(sourceUv[i]);else allUv=false;}
            for(int i=0;i<sourceIndices.Length;i+=3){int a=sourceIndices[i],b=sourceIndices[i+1],c=sourceIndices[i+2];if(a<0||b<0||c<0||a>=sourcePositions.Length||b>=sourcePositions.Length||c>=sourcePositions.Length)throw new InvalidDataException("GLB primitive index is out of range.");triangles.Add(vertexBase+a);triangles.Add(vertexBase+c);triangles.Add(vertexBase+b);}
        }

        private static Matrix4x4 NodeMatrix(JObject node)
        {
            JArray matrix=(JArray)node["matrix"];if(matrix!=null){if(matrix.Count!=16)throw new InvalidDataException("GLB node matrix must contain 16 values.");Matrix4x4 result=new Matrix4x4();for(int column=0;column<4;column++)for(int row=0;row<4;row++)result[row,column]=(float)matrix[column*4+row];return result;}
            Vector3 translation=ReadArray3((JArray)node["translation"],Vector3.zero);Vector3 scale=ReadArray3((JArray)node["scale"],Vector3.one);Quaternion rotation=ReadQuaternion((JArray)node["rotation"]);return Matrix4x4.TRS(translation,rotation,scale);
        }
        private static Vector3 ReadArray3(JArray value,Vector3 fallback){return value==null?fallback:value.Count==3?new Vector3((float)value[0],(float)value[1],(float)value[2]):throw new InvalidDataException("GLB vector must contain three values.");}
        private static Quaternion ReadQuaternion(JArray value){return value==null?Quaternion.identity:value.Count==4?new Quaternion((float)value[0],(float)value[1],(float)value[2],(float)value[3]):throw new InvalidDataException("GLB quaternion must contain four values.");}
        private static Vector3[] ReadVec3(int accessor,JArray accessors,JArray views,byte[] bin){Accessor info=GetAccessor(accessor,"VEC3",5126,accessors,views,bin);Vector3[] result=new Vector3[info.Count];for(int i=0;i<result.Length;i++){int o=info.Offset+i*info.Stride;result[i]=new Vector3(ReadF32(bin,o),ReadF32(bin,o+4),ReadF32(bin,o+8));}return result;}
        private static Vector2[] ReadVec2(int accessor,JArray accessors,JArray views,byte[] bin){Accessor info=GetAccessor(accessor,"VEC2",5126,accessors,views,bin);Vector2[] result=new Vector2[info.Count];for(int i=0;i<result.Length;i++){int o=info.Offset+i*info.Stride;result[i]=new Vector2(ReadF32(bin,o),ReadF32(bin,o+4));}return result;}
        private static int[] ReadIndices(int accessor,JArray accessors,JArray views,byte[] bin){if(accessor<0||accessor>=accessors.Count)throw new InvalidDataException("GLB accessor index is invalid.");JObject a=(JObject)accessors[accessor];if((string)a["type"]!="SCALAR"||a["sparse"]!=null)throw new NotSupportedException("Sparse or non-scalar GLB indices are unsupported.");int component=(int)a["componentType"];int size=component==5121?1:component==5123?2:component==5125?4:throw new NotSupportedException("GLB index component type is unsupported.");Accessor info=GetAccessor(accessor,"SCALAR",component,accessors,views,bin);int[] result=new int[info.Count];for(int i=0;i<result.Length;i++){int o=info.Offset+i*info.Stride;uint value=component==5121?bin[o]:component==5123?(uint)(bin[o]|bin[o+1]<<8):ReadU32(bin,o);if(value>int.MaxValue)throw new InvalidDataException("GLB index exceeds Int32.");result[i]=(int)value;}return result;}
        private struct Accessor{public int Offset;public int Count;public int Stride;}
        private static Accessor GetAccessor(int index,string type,int component,JArray accessors,JArray views,byte[] bin){if(index<0||index>=accessors.Count)throw new InvalidDataException("GLB accessor index is invalid.");JObject a=(JObject)accessors[index];if((string)a["type"]!=type||(int)a["componentType"]!=component||a["sparse"]!=null)throw new NotSupportedException("GLB accessor type, component type, or sparse storage is unsupported.");int viewIndex=(int?)a["bufferView"]??throw new InvalidDataException("GLB accessor has no bufferView.");if(viewIndex<0||viewIndex>=views.Count)throw new InvalidDataException("GLB bufferView index is invalid.");JObject view=(JObject)views[viewIndex];int count=(int)a["count"];int components=type=="VEC3"?3:type=="VEC2"?2:1;int element=components*(component==5126||component==5125?4:component==5123?2:1);int stride=(int?)view["byteStride"]??element;if(stride<element)throw new InvalidDataException("GLB byteStride is smaller than the accessor element.");int offset=((int?)view["byteOffset"]??0)+((int?)a["byteOffset"]??0);long end=(long)offset+(long)(count-1)*stride+element;if(count<=0||offset<0||end>bin.Length)throw new InvalidDataException("GLB accessor extends beyond the BIN chunk.");return new Accessor{Offset=offset,Count=count,Stride=stride};}
        private static Texture2D ReadEmbeddedTexture(int textureIndex,JObject root,JArray views,byte[] bin){JArray textures=(JArray)root["textures"]??new JArray();JArray images=(JArray)root["images"]??new JArray();if(textureIndex<0||textureIndex>=textures.Count)throw new InvalidDataException("GLB texture index is invalid.");int imageIndex=(int?)textures[textureIndex]["source"]??throw new InvalidDataException("GLB texture has no image source.");if(imageIndex<0||imageIndex>=images.Count)throw new InvalidDataException("GLB image index is invalid.");JObject image=(JObject)images[imageIndex];if(image["uri"]!=null)throw new NotSupportedException("External/data URI GLB images are not supported; embed the image as a bufferView.");string mime=(string)image["mimeType"];if(mime!="image/png"&&mime!="image/jpeg")throw new NotSupportedException("Only embedded PNG/JPEG BaseColor images are supported by the Editor converter.");int viewIndex=(int)image["bufferView"];if(viewIndex<0||viewIndex>=views.Count)throw new InvalidDataException("GLB image bufferView is invalid.");JObject view=(JObject)views[viewIndex];int offset=(int?)view["byteOffset"]??0;int length=(int)view["byteLength"];if(offset<0||length<=0||offset+length>bin.Length)throw new InvalidDataException("GLB image extends beyond the BIN chunk.");byte[] encoded=new byte[length];Buffer.BlockCopy(bin,offset,encoded,0,length);Texture2D texture=new Texture2D(2,2,TextureFormat.RGBA32,false);if(!texture.LoadImage(encoded,false)){UnityEngine.Object.DestroyImmediate(texture);throw new InvalidDataException("Unity could not decode the embedded BaseColor image.");}return texture;}
        private static int[] Sequential(int count){int[] values=new int[count];for(int i=0;i<count;i++)values[i]=i;return values;}
        private static uint ReadU32(byte[] data,int offset){return (uint)data[offset]|((uint)data[offset+1]<<8)|((uint)data[offset+2]<<16)|((uint)data[offset+3]<<24);}
        private static float ReadF32(byte[] data,int offset){return BitConverter.ToSingle(data,offset);}
    }
}
