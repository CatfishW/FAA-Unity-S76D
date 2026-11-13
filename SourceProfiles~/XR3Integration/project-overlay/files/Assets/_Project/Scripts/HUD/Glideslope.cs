using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Glideslope : MonoBehaviour
{
    public GameObject glideslopeBar;
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {

    }

    /// <summary>Non-finite deviation means no glideslope: the bar is removed, never drawn on-glidepath.</summary>
    public void UpdateGlideslope(float dots)
    {
        if (glideslopeBar == null) return;
        bool valid = !float.IsNaN(dots) && !float.IsInfinity(dots);
        if (glideslopeBar.activeSelf != valid) glideslopeBar.SetActive(valid);
        if (!valid) return;
        float dotnum = dots * 0.09f;
        glideslopeBar.transform.localPosition = new Vector3(glideslopeBar.transform.localPosition.x, dotnum, glideslopeBar.transform.localPosition.z);
    }


}
