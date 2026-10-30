using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SlipSkidHUD : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    /// <summary>Non-finite slip means no measurement: the brick is removed, never parked at a false centre.</summary>
    public void UpdateSlip(float slip)
    {
        bool valid = !float.IsNaN(slip) && !float.IsInfinity(slip);
        if (!_visibilityKnown || valid != _visible) { SetGraphicsVisible(gameObject, valid); _visible = valid; _visibilityKnown = true; }
        if (valid) this.gameObject.transform.localPosition = new Vector3(-5 * slip, -10, 0);
    }

    private bool _visible, _visibilityKnown;

    internal static void SetGraphicsVisible(GameObject root, bool visible)
    {
        foreach (var graphic in root.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            if (graphic.enabled != visible) graphic.enabled = visible;
    }
}
