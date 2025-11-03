using UnityEngine;
using System.Collections;

public class Melter : MonoBehaviour {
	
	public Transform heatPoint;
	public bool restoreColor=false;
	
	void Start () {
		var mesh = GetComponent<MeshFilter>().mesh;
		var cols = new Color[mesh.vertexCount];
		for (int i = 0; i < mesh.vertexCount; i++) 
		{
			cols[i] = Color.white;
		}
		mesh.colors = cols;
		UpdateColor();
	}
	
	void Update () {
		UpdateColor();
	}
	
	
	void UpdateColor()
	{
		float viewDistance = 1f;
		float fadeSpeed = 1f;
		
		var mesh = GetComponent<MeshFilter>().mesh;
		var verts = mesh.vertices;
		var cols = mesh.colors;
		
		for (int i = 0; i < mesh.vertexCount; i++) 
		{
			//float distance = Vector3.Distance(heatPoint.position.normalized, verts[i].normalized);
			float dist = Mathf.Exp(Vector3.Distance(heatPoint.position, verts[i])); // broken
			float distance = Remap (dist,0f,3f,0f,1f);
			
			if (restoreColor)
			{
				cols[i].a = Mathf.Clamp01(cols[i].a-(viewDistance-distance)*Time.deltaTime*fadeSpeed);
			}else{
				cols[i].a = Mathf.Min(cols[i].a,Mathf.Clamp01(cols[i].a-(viewDistance-distance)*Time.deltaTime*fadeSpeed));
			}
		}
		mesh.colors = cols;
	}
	
	float Remap(float val, float low1, float high1, float low2, float high2)
	{
		return low2 + (val-low1)*(high2-low2)/(high1-low1);
	}
}