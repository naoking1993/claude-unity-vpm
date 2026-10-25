using UdonSharp;
using UnityEngine;

public abstract class GoodBase : UdonSharpBehaviour
{
    [SerializeField] protected int baseValue = 3;
    public virtual int Compute(int x) => x + baseValue;
    protected abstract void Apply();
}
