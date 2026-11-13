using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Retired legacy FMA text binder (never driven: no caller and no scene instance). The live Digital FMA is
/// FAA.Customization.FaaFlightModeAnnunciator, composed by FaaFlightModeAnnunciation from the autopilot state.
/// Kept only so old prefabs keep their script reference; all writes are null-safe.
/// </summary>
public class AP_Mode : MonoBehaviour
{

    public Text Collective_Active;
    public Text Collective_Armed;

    public Text Roll_Active;
    public Text Roll_Armed;

    public Text Pitch_Active;
    public Text Pitch_Armed;

    public Text Cpl;


    void Start()
    {

    }


    // Update is called once per frame
    void Update()
    {

    }


    // Update is called once per frame
    public void UpdateAP_Mode(string Collective_Active_String, string Collective_Armed_String, string Roll_Active_String, string Roll_Armed_String, string Pitch_Active_String, string Pitch_Armed_String, float cps_float)
    {
        if (Collective_Active != null) Collective_Active.text = Collective_Active_String;
        if (Collective_Armed != null) Collective_Armed.text = Collective_Armed_String;

        if (Roll_Active != null) Roll_Active.text = Roll_Active_String;
        if (Roll_Armed != null) Roll_Armed.text = Roll_Armed_String;

        if (Pitch_Active != null) Pitch_Active.text = Pitch_Active_String;
        if (Pitch_Armed != null) Pitch_Armed.text = Pitch_Armed_String;

        if (Cpl != null) Cpl.text = cps_float == 1 ? "CPL" : "";
    }
}
