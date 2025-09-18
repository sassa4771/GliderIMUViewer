using UnityEngine;

public class LocalZInputMover : MonoBehaviour
{
    public float speed = 3f;
    public string forwardKey = "w";
    public string backwardKey = "s";

    void Update()
    {
        float v = 0f;
        if (Input.GetKey(forwardKey))  v -= 1f;
        if (Input.GetKey(backwardKey)) v += 1f;

        if (Mathf.Abs(v) > 0f)
            transform.Translate(Vector3.forward * (v * speed * Time.deltaTime), Space.Self);
    }
}
